using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.AI.Interfaces;
using Morgana.AI.Telemetry;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

/// <summary>
/// SQLite-backed dust limiter: a per-conversation LIFETIME budget (no sliding window, no reset)
/// in the single-row <c>dust_budget</c> table, sharing the same per-conversation database as
/// <see cref="SQLiteConversationPersistenceService"/>. "Let-it-finish": a turn already running
/// completes even if it pushes the total over budget — only the NEXT turn gets blocked, by
/// <see cref="IsOverBudgetAsync"/>. Every operation fails open on a storage fault.
/// </summary>
public class SQLiteDustLimitService : IDustLimitService
{
    /// <summary>
    /// Logger for charges, threshold crossings and the fail-open path — a storage fault that
    /// silently stops metering is otherwise invisible.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Dust policy from <c>Morgana:DustLimiting</c>: enable flag, per-conversation budget and the
    /// three user-facing texts (70% warning, 90% warning, lockout).
    /// </summary>
    private readonly DustLimitingOptions options;

    /// <summary>
    /// Persistence configuration, read only for <c>StoragePath</c>: the budget tables live in the
    /// conversation's own database, not in one of their own.
    /// </summary>
    private readonly ConversationPersistenceOptions persistenceOptions;

    /// <summary>
    /// Owner of the database file. Delegated to for schema creation, since a dust charge can land
    /// before anything else has had reason to create the conversation's database.
    /// </summary>
    private readonly IConversationPersistenceService persistenceService;

    /// <summary>
    /// Initializes the dust limiter with its policy and the persistence service that owns
    /// the per-conversation SQLite database lifecycle.
    /// </summary>
    public SQLiteDustLimitService(
        IOptions<DustLimitingOptions> options,
        IOptions<ConversationPersistenceOptions> persistenceOptions,
        IConversationPersistenceService persistenceService,
        ILogger<SQLiteDustLimitService> logger)
    {
        this.options = options.Value;
        this.persistenceOptions = persistenceOptions.Value;
        this.persistenceService = persistenceService;
        this.logger = logger;

        // The budget every conversation of this installation gets, stated once at startup: from here it
        // is only ever compared against, never printed.
        logger.LogInformation(
            "SQLiteDustLimitService initialized: enabled={Enabled}, budget={Budget}",
            this.options.Enabled, this.options.BudgetPerConversation);
    }

    /// <inheritdoc/>
    public async Task ChargeAsync(string conversationId, double dust, string llmRole)
    {
        // Nothing to book. A zero or negative charge reaches this method from a provider that reported
        // no usage, which is silence rather than a free turn.
        if (!options.Enabled || dust <= 0)
            return;

        // Never materialize a database for a non-conversation. The
        // CompleteWithSystemPromptAsync path is reached with framework-internal logging
        // labels too (e.g. the presenter passes the literal "presentation"); dust is only
        // chargeable against a real, already-handshaken conversation. A real conversation's
        // DB always exists by the time any chargeable LLM call runs (created at the
        // conversation/start handshake), so legitimate per-turn charges still land.
        if (!persistenceService.ConversationExists(conversationId))
            return;

        try
        {
            await persistenceService.EnsureDatabaseInitializedAsync(conversationId);

            await using SqliteConnection connection = new SqliteConnection(GetConnectionString(conversationId));
            await connection.OpenAsync();

            // The running total and the line accounting for it are written together: a total nobody can
            // break down is a bill without its items.
            await using SqliteTransaction transaction = connection.BeginTransaction();
            try
            {
                // The id=1 row is seeded once at schema init (EnsureDatabaseInitializedAsync
                // above guarantees it exists), so the hot path is a bare UPDATE.
                await using (SqliteCommand updateCommand = connection.CreateCommand())
                {
                    updateCommand.Transaction = transaction;

                    // Added in the database rather than read, summed and written back: two agents of one
                    // conversation can charge at the same moment. Neither may overwrite the other.
                    updateCommand.CommandText = "UPDATE dust_budget SET dust_consumed = dust_consumed + @dust WHERE id = 1;";
                    updateCommand.Parameters.AddWithValue("@dust", dust);
                    await updateCommand.ExecuteNonQueryAsync();
                }

                // A second write beside the counter, in the same transaction: the counter answers what
                // is left, this answers where it went — per role, which is what makes a bill readable.
                await using (SqliteCommand logCommand = connection.CreateCommand())
                {
                    logCommand.Transaction = transaction;
                    logCommand.CommandText =
                        "INSERT INTO dust_usage_log (timestamp, dust_consumed, llm_role) VALUES (@ts, @dust, @role);";
                    logCommand.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
                    logCommand.Parameters.AddWithValue("@dust", dust);
                    logCommand.Parameters.AddWithValue("@role", llmRole);
                    await logCommand.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();

                // Emitted after the commit, never before: a metric reporting spend the ledger rolled back
                // would make a conversation look more expensive than its own books say.
                MorganaTelemetry.DustConsumed.Add(
                    dust,
                    new KeyValuePair<string, object?>(MorganaTelemetry.DustLlmRole, llmRole),
                    new KeyValuePair<string, object?>(MorganaTelemetry.ConversationId, conversationId));

                logger.LogDebug(
                    "Charged {Dust:F4} dust to {ConversationId} (role={LlmRole})", dust, conversationId, llmRole);
            }
            catch
            {
                // Charged whole or not at all: a total raised without its usage line would leave spend
                // nobody can attribute to a role.
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            // The turn already ran and the tokens are already spent. Failing the conversation over the
            // bookkeeping would cost the user a turn they cannot get back for a figure nobody sees.
            logger.LogError(ex, "Dust charge failed for {ConversationId} — failing open", conversationId);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsOverBudgetAsync(string conversationId)
    {
        // Unmetered, so no conversation can be over a budget nobody is keeping.
        if (!options.Enabled)
            return false;

        // No DB for a non-existent conversation → nothing consumed → not over budget.
        if (!persistenceService.ConversationExists(conversationId))
            return false;

        try
        {
            // At the budget, not merely over it: the threshold is the last turn admitted, so a
            // conversation that has spent exactly its allowance opens no further turn.
            double consumed = await ReadConsumedAsync(conversationId);
            return consumed >= options.BudgetPerConversation;
        }
        catch (Exception ex)
        {
            // A ledger that cannot be read must not lock a user out of a conversation that may well have
            // budget left.
            logger.LogError(ex, "Dust budget check failed for {ConversationId} — failing open", conversationId);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<double> GetConsumedAsync(string conversationId)
    {
        // Nothing was metered with the limiter off and a conversation with no database has burned
        // nothing yet — both answer zero rather than reaching for a table that may not exist.
        if (!options.Enabled || !persistenceService.ConversationExists(conversationId))
            return 0.0;

        try
        {
            // The same cumulative total the budget check reads, handed over in units instead of as a
            // fraction of a budget: a caller asking what was spent may have no budget in mind at all.
            return await ReadConsumedAsync(conversationId);
        }
        catch (Exception ex)
        {
            // Fails open like every other method here: a storage fault must never become a turn that
            // does not happen and reporting zero only ever under-states a cost, never invents one.
            logger.LogError(ex, "Dust consumption query failed for {ConversationId} — returning 0.0", conversationId);
            return 0.0;
        }
    }

    /// <inheritdoc/>
    public async Task<double> GetConsumedSinceAsync(string conversationId, double baseline)
    {
        // Clamped here rather than at the call site: a ledger that appears to run backwards is this
        // service's problem to absorb — a disabled limiter, a failed read, a database that vanished
        // between the two — and never a negative cost handed to whoever asked what the work cost.
        double consumed = await GetConsumedAsync(conversationId);
        return consumed > baseline ? consumed - baseline : 0.0;
    }

    /// <inheritdoc/>
    public async Task<double> GetUsageRatioAsync(string conversationId)
    {
        // Unmetered, or metered against no budget at all: either way there is no proportion to report.
        if (!options.Enabled || options.BudgetPerConversation <= 0)
            return 0.0;

        // No DB for a non-existent conversation → nothing consumed → ratio 0.
        if (!persistenceService.ConversationExists(conversationId))
            return 0.0;

        try
        {
            // A fraction rather than a figure: what reads this decides whether to warn, so it needs how
            // far along the conversation is rather than how much it burned.
            double consumed = await ReadConsumedAsync(conversationId);
            return consumed / options.BudgetPerConversation;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Dust ratio query failed for {ConversationId} — returning 0.0", conversationId);
            return 0.0;
        }
    }

    /// <inheritdoc/>
    public async Task<(bool Send70, bool Send90)> CheckAndMarkWarningsAsync(string conversationId)
    {
        if (!options.Enabled || options.BudgetPerConversation <= 0)
            return (false, false);

        // No DB for a non-existent conversation → no usage → nothing to warn about.
        if (!persistenceService.ConversationExists(conversationId))
            return (false, false);

        try
        {
            await persistenceService.EnsureDatabaseInitializedAsync(conversationId);

            await using SqliteConnection connection = new SqliteConnection(GetConnectionString(conversationId));
            await connection.OpenAsync();
            await using SqliteTransaction transaction = connection.BeginTransaction();
            try
            {
                double consumed;
                bool warning70Sent;
                bool warning90Sent;

                await using (SqliteCommand readCommand = connection.CreateCommand())
                {
                    readCommand.Transaction = transaction;
                    readCommand.CommandText = "SELECT dust_consumed, warning_70_sent, warning_90_sent FROM dust_budget WHERE id = 1;";
                    await using SqliteDataReader reader = await readCommand.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        await transaction.RollbackAsync();
                        return (false, false); // No usage yet → nothing to warn about
                    }

                    consumed = reader.GetDouble(0);
                    warning70Sent = reader.GetInt32(1) != 0;
                    warning90Sent = reader.GetInt32(2) != 0;
                }

                // Each threshold fires once in a conversation's life, which is what the two stored flags
                // record: crossing 70% again after a charge must not warn a user already warned.
                double ratio = consumed / options.BudgetPerConversation;
                bool send70 = ratio >= 0.70 && !warning70Sent;
                bool send90 = ratio >= 0.90 && !warning90Sent;

                // Read and mark in one transaction, so two turns charging concurrently cannot both
                // decide to send the same warning. Each CASE leaves the other flag as it found it.
                if (send70 || send90)
                {
                    await using SqliteCommand updateCommand = connection.CreateCommand();
                    updateCommand.Transaction = transaction;
                    updateCommand.CommandText =
                        "UPDATE dust_budget SET " +
                        "warning_70_sent = CASE WHEN @set70 = 1 THEN 1 ELSE warning_70_sent END, " +
                        "warning_90_sent = CASE WHEN @set90 = 1 THEN 1 ELSE warning_90_sent END " +
                        "WHERE id = 1;";
                    updateCommand.Parameters.AddWithValue("@set70", send70 ? 1 : 0);
                    updateCommand.Parameters.AddWithValue("@set90", send90 ? 1 : 0);
                    await updateCommand.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
                return (send70, send90);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Warning threshold check failed for {ConversationId} — failing open", conversationId);
            return (false, false);
        }
    }

    /// <summary>Reads the conversation's cumulative dust, initializing the schema if this is its first read.</summary>
    /// <param name="conversationId">Conversation whose budget row is read.</param>
    /// <returns>Dust consumed so far; zero when the row does not exist yet.</returns>
    private async Task<double> ReadConsumedAsync(string conversationId)
    {
        await persistenceService.EnsureDatabaseInitializedAsync(conversationId);

        await using SqliteConnection connection = new SqliteConnection(GetConnectionString(conversationId));
        await connection.OpenAsync();

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT dust_consumed FROM dust_budget WHERE id = 1;";
        object? result = await command.ExecuteScalarAsync();

        return result is null || result == DBNull.Value ? 0.0 : Convert.ToDouble(result);
    }

    /// <summary>
    /// Points at the conversation's own database. The identifier is sanitized here as it is in the
    /// persistence service: it reaches this layer from a channel and it becomes a file name.
    /// </summary>
    /// <param name="conversationId">Conversation whose database is addressed.</param>
    private string GetConnectionString(string conversationId)
    {
        string sanitized = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
        return $"Data Source={Path.Combine(persistenceOptions.StoragePath, $"morgana-{sanitized}.db")}";
    }
}