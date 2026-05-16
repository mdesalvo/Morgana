using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

/// <summary>
/// SQLite-backed dust limiter. Stores the per-conversation lifetime token budget in the
/// same per-conversation database used by <see cref="SQLiteConversationPersistenceService"/>,
/// mirroring how <see cref="SQLiteRateLimitService"/> shares that file.
/// </summary>
/// <remarks>
/// <para><strong>Storage:</strong></para>
/// <code>
/// {StoragePath}/morgana-{conversationId}.db
///   ├─ dust_budget     — single row (id=1): running total + one-shot warning flags
///   └─ dust_usage_log  — per-call audit trail (diagnostics / OTel only, not enforcement)
/// </code>
/// <para><strong>Semantics:</strong> the budget is a lifetime resource — no sliding window,
/// no reset. "Let-it-finish": a turn already running completes even if it pushes the total
/// over budget; the <em>next</em> turn is blocked by <see cref="IsOverBudgetAsync"/>.</para>
/// <para>Every operation fails open: a storage fault is logged and treated as "allow", so the
/// limiter can never become a single point of failure mid-conversation.</para>
/// </remarks>
public class SQLiteDustLimitService : IDustLimitService
{
    private readonly ILogger logger;
    private readonly DustLimitingOptions options;
    private readonly ConversationPersistenceOptions persistenceOptions;
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

        logger.LogInformation(
            "SQLiteDustLimitService initialized: enabled={Enabled}, budget={Budget}",
            this.options.Enabled, this.options.BudgetPerConversation);
    }

    /// <inheritdoc/>
    public async Task ChargeAsync(string conversationId, double dust, string llmRole)
    {
        if (!options.Enabled || dust <= 0)
            return;

        try
        {
            await persistenceService.EnsureDatabaseInitializedAsync(conversationId);

            await using SqliteConnection connection = new SqliteConnection(GetConnectionString(conversationId));
            await connection.OpenAsync();
            await using SqliteTransaction transaction = connection.BeginTransaction();
            try
            {
                // The id=1 row is seeded once at schema init (EnsureDatabaseInitializedAsync
                // above guarantees it exists), so the hot path is a bare UPDATE.
                await using (SqliteCommand updateCommand = connection.CreateCommand())
                {
                    updateCommand.Transaction = transaction;
                    updateCommand.CommandText = "UPDATE dust_budget SET dust_consumed = dust_consumed + @dust WHERE id = 1;";
                    updateCommand.Parameters.AddWithValue("@dust", dust);
                    await updateCommand.ExecuteNonQueryAsync();
                }

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

                logger.LogDebug(
                    "Charged {Dust:F4} dust to {ConversationId} (role={LlmRole})", dust, conversationId, llmRole);
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Dust charge failed for {ConversationId} — failing open", conversationId);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsOverBudgetAsync(string conversationId)
    {
        if (!options.Enabled)
            return false;

        try
        {
            double consumed = await ReadConsumedAsync(conversationId);
            return consumed >= options.BudgetPerConversation;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Dust budget check failed for {ConversationId} — failing open", conversationId);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<double> GetUsageRatioAsync(string conversationId)
    {
        if (!options.Enabled || options.BudgetPerConversation <= 0)
            return 0.0;

        try
        {
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
    public async Task<(bool Send80, bool Send90)> CheckAndMarkWarningsAsync(string conversationId)
    {
        if (!options.Enabled || options.BudgetPerConversation <= 0)
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
                bool warning80Sent;
                bool warning90Sent;

                await using (SqliteCommand readCommand = connection.CreateCommand())
                {
                    readCommand.Transaction = transaction;
                    readCommand.CommandText = "SELECT dust_consumed, warning_80_sent, warning_90_sent FROM dust_budget WHERE id = 1;";
                    await using SqliteDataReader reader = await readCommand.ExecuteReaderAsync();
                    if (!await reader.ReadAsync())
                    {
                        await transaction.RollbackAsync();
                        return (false, false); // No usage yet → nothing to warn about
                    }

                    consumed = reader.GetDouble(0);
                    warning80Sent = reader.GetInt32(1) != 0;
                    warning90Sent = reader.GetInt32(2) != 0;
                }

                double ratio = consumed / options.BudgetPerConversation;
                bool send80 = ratio >= 0.80 && !warning80Sent;
                bool send90 = ratio >= 0.90 && !warning90Sent;

                if (send80 || send90)
                {
                    await using SqliteCommand updateCommand = connection.CreateCommand();
                    updateCommand.Transaction = transaction;
                    updateCommand.CommandText =
                        "UPDATE dust_budget SET " +
                        "warning_80_sent = CASE WHEN @set80 = 1 THEN 1 ELSE warning_80_sent END, " +
                        "warning_90_sent = CASE WHEN @set90 = 1 THEN 1 ELSE warning_90_sent END " +
                        "WHERE id = 1;";
                    updateCommand.Parameters.AddWithValue("@set80", send80 ? 1 : 0);
                    updateCommand.Parameters.AddWithValue("@set90", send90 ? 1 : 0);
                    await updateCommand.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();
                return (send80, send90);
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

    private string GetConnectionString(string conversationId)
    {
        string sanitized = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
        return $"Data Source={Path.Combine(persistenceOptions.StoragePath, $"morgana-{sanitized}.db")}";
    }
}
