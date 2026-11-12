using Microsoft.Data.Sqlite;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

/// <summary>
/// Keeps each conversation's lifetime dust budget, with no window and no reset, in the <c>dust_budget</c> row of its own database.
/// A running turn finishes even when it overruns the budget: only the next one is refused. Every operation fails open on a storage fault.
/// </summary>
public class SQLiteDustLimitService : IDustLimitService
{
    /// <summary>
    /// Logs the charges and the fail-open path.
    /// A storage fault that stops the metering would otherwise be invisible.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>The policy of <c>Morgana:DustLimiting</c>: the switch, the budget per conversation and the texts of the warnings and of the lockout.</summary>
    private readonly DustLimitingOptions options;

    /// <summary>The owner of the conversation's database, which opens it at the current schema with the budget row seeded.</summary>
    private readonly SQLiteConversationPersistenceService persistenceService;

    /// <summary>Binds the limiter to its policy and to the conversation's database, where the budget tables live.</summary>
    public SQLiteDustLimitService(
        IOptions<DustLimitingOptions> options,
        SQLiteConversationPersistenceService persistenceService,
        ILogger<SQLiteDustLimitService> logger)
    {
        this.options = options.Value;
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

        // A label that names no conversation gets no database: the presenter charges under "presentation".
        // A real conversation's database exists from its handshake, so every charge of a turn still lands.
        if (!await persistenceService.ConversationExistsAsync(conversationId))
            return;

        try
        {
            // The budget lives in the conversation's own database, opened at a schema that seeds the budget row
            // before the first charge.
            await using SqliteConnection connection = await persistenceService.OpenDatabaseAsync(conversationId);

            // The running total and the line accounting for it are written together: a total nobody can
            // break down is a bill without its items.
            await using SqliteTransaction transaction = connection.BeginTransaction();
            try
            {
                // The budget row is seeded when the schema is created, so a charge only has to raise it.
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

                    // Stored as text: invariant, so the log reads the same whichever host wrote a row.
                    logCommand.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                    logCommand.Parameters.AddWithValue("@dust", dust);
                    logCommand.Parameters.AddWithValue("@role", llmRole);
                    await logCommand.ExecuteNonQueryAsync();
                }

                // The total and its line become visible together.
                await transaction.CommitAsync();

                // Emitted after the commit, never before: a metric reporting spend the ledger rolled back
                // would make a conversation look more expensive than its own books say.
                Telemetry.DustConsumed.Add(
                    dust,
                    new KeyValuePair<string, object?>(Telemetry.DustLlmRole, llmRole),
                    new KeyValuePair<string, object?>(Telemetry.ConversationId, conversationId));

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

        // A budget of zero is spent before anything is charged, whether or not the conversation has a
        // record yet: nothing it could ask for is admitted.
        if (options.BudgetPerConversation <= 0)
            return true;

        // A conversation with no database has spent nothing.
        if (!await persistenceService.ConversationExistsAsync(conversationId))
            return false;

        try
        {
            // At the budget, not merely over it: the threshold is the last turn admitted, so a
            // conversation that has spent exactly its allowance opens no further turn.
            double consumedDust = await ReadConsumedAsync(conversationId);
            return consumedDust >= options.BudgetPerConversation;
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
        if (!options.Enabled || !await persistenceService.ConversationExistsAsync(conversationId))
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
        // service's problem to absorb (a disabled limiter, a failed read, a database that vanished
        // between the two), never a negative cost handed to whoever asked what the work cost.
        double consumedDust = await GetConsumedAsync(conversationId);
        return consumedDust > baseline ? consumedDust - baseline : 0.0;
    }

    /// <inheritdoc/>
    public async Task<double?> GetRemainingLevelAsync(string conversationId)
    {
        // Unmetered, so there is no gauge to show.
        if (!options.Enabled)
            return null;

        // A budget of zero is spent before anything is charged, as the gate already reads it:
        // the gauge and the resume say so too, rather than show a conversation nobody measures.
        if (options.BudgetPerConversation <= 0)
            return 0.0;

        // The share of the budget already spent, zero when the ledger cannot be read.
        double usageRatio = await ReadUsageRatioAsync();

        // Floored rather than rounded, so a sub-percent residual reads as spent exactly when the
        // next turn will be refused as over budget.
        double remaining = Math.Clamp(1.0 - usageRatio, 0.0, 1.0);
        return Math.Floor(remaining * 100.0) / 100.0;

        #region Utilities
        // Reads the share of the budget spent so far, failing open to zero.
        async Task<double> ReadUsageRatioAsync()
        {
            // A conversation with no database has spent nothing.
            if (!await persistenceService.ConversationExistsAsync(conversationId))
                return 0.0;

            try
            {
                // The total over the budget, which the warnings and the gauge are both read on.
                double consumedDust = await ReadConsumedAsync(conversationId);
                return consumedDust / options.BudgetPerConversation;
            }
            catch (Exception ex)
            {
                // A ledger that cannot be read shows a full gauge rather than a lockout nobody decided.
                logger.LogError(ex, "Dust ratio query failed for {ConversationId} — returning 0.0", conversationId);
                return 0.0;
            }
        }
        #endregion
    }

    /// <inheritdoc/>
    public async Task<(bool Send70, bool Send90)> CheckAndMarkWarningsAsync(string conversationId)
    {
        // A budget of zero never had a 70% to cross: the conversation is spent from its start and the
        // exhaustion notice is the only thing left to tell.
        if (!options.Enabled || options.BudgetPerConversation <= 0)
            return (false, false);

        // A conversation with no database has spent nothing to warn about.
        if (!await persistenceService.ConversationExistsAsync(conversationId))
            return (false, false);

        try
        {
            // The warning flags live on the budget row, which the schema seeds in the conversation's own database.
            await using SqliteConnection connection = await persistenceService.OpenDatabaseAsync(conversationId);

            // Read and mark in one transaction, so two turns charging concurrently cannot both decide to send the same warning.
            await using SqliteTransaction transaction = connection.BeginTransaction();
            try
            {
                // The total and the two flags, read together from the budget row.
                double consumedDust;
                bool warning70Sent;
                bool warning90Sent;

                await using (SqliteCommand readCommand = connection.CreateCommand())
                {
                    readCommand.Transaction = transaction;
                    readCommand.CommandText = "SELECT dust_consumed, warning_70_sent, warning_90_sent FROM dust_budget WHERE id = 1;";
                    await using SqliteDataReader reader = await readCommand.ExecuteReaderAsync();

                    // A missing budget row has recorded no usage, so there is nothing to warn about.
                    if (!await reader.ReadAsync())
                    {
                        await transaction.RollbackAsync();
                        return (false, false);
                    }

                    // The flags are stored as integers.
                    consumedDust = reader.GetDouble(0);
                    warning70Sent = reader.GetInt32(1) != 0;
                    warning90Sent = reader.GetInt32(2) != 0;
                }

                // Each threshold fires once in a conversation's life, which is what the two stored flags
                // record: crossing 70% again after a charge must not warn a user already warned.
                double usageRatio = consumedDust / options.BudgetPerConversation;
                bool send70 = usageRatio >= 0.70 && !warning70Sent;
                bool send90 = usageRatio >= 0.90 && !warning90Sent;

                // A threshold crossed now is marked as warned; each CASE leaves the other flag as it found it.
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

                // The marks become visible with the decision, which the manager acts on.
                await transaction.CommitAsync();
                return (send70, send90);
            }
            catch
            {
                // Nothing is marked as warned unless the warning is decided.
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            // A ledger that cannot be read sends no warning: warnings are advisory and the gate still refuses an exhausted conversation.
            logger.LogError(ex, "Warning threshold check failed for {ConversationId} — failing open", conversationId);
            return (false, false);
        }
    }

    /// <summary>Reads the conversation's cumulative dust, zero when the budget row does not exist yet.</summary>
    private async Task<double> ReadConsumedAsync(string conversationId)
    {
        // The spent total is read from the budget row of the conversation's own database,
        // whose first read may precede any other use of it.
        await using SqliteConnection connection = await persistenceService.OpenDatabaseAsync(conversationId);

        // One row holds the conversation's whole budget.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT dust_consumed FROM dust_budget WHERE id = 1;";
        object? consumedDust = await command.ExecuteScalarAsync();

        // A missing row or value has spent nothing.
        return consumedDust is null || consumedDust == DBNull.Value ? 0.0 : Convert.ToDouble(consumedDust, CultureInfo.InvariantCulture);
    }
}