using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.Framework.Interfaces;
using static Morgana.Framework.Records;

namespace Morgana.Framework.Services;

/// <summary>
/// SQLite-based rate limiting service with persistent request tracking.
/// Stores request logs in the same database as conversation data for consistency.
/// Delegates database initialization to IConversationPersistenceService.
/// </summary>
/// <remarks>
/// <para><strong>Storage Model:</strong></para>
/// <code>
/// Database per conversation (same as agent persistence):
///   {StoragePath}/morgana-conv12345.db
///     ├─ table: morgana (agent sessions)
///     └─ table: rate_limit_log (request timestamps)
/// </code>
/// <para><strong>Sliding Window Algorithm:</strong></para>
/// <para>On each check:</para>
/// <list type="number">
/// <item>Ensure database exists (via persistence service)</item>
/// <item>Delete expired requests (older than 24 hours)</item>
/// <item>Count requests in each time window (minute, hour, day)</item>
/// <item>If any limit exceeded, deny request</item>
/// <item>If allowed, insert current request timestamp</item>
/// </list>
/// <para><strong>Database Initialization:</strong></para>
/// <para>Delegates to IConversationPersistenceService.EnsureDatabaseInitializedAsync()
/// to handle schema creation and versioning. This prevents duplication of schema logic
/// and ensures consistency with agent persistence.</para>
/// <para><strong>Race Condition Prevention:</strong></para>
/// <para>Rate limiting is checked BEFORE any agent executes. The database might not exist yet
/// (created only when first agent saves thread). Calling EnsureDatabaseInitializedAsync()
/// prevents FileNotFoundException by creating the database on-demand.</para>
/// </remarks>
public class SQLiteRateLimitService : IRateLimitService
{
    private readonly ILogger logger;
    private readonly RateLimitOptions options;
    private readonly ConversationPersistenceOptions persistenceOptions;
    private readonly IConversationPersistenceService persistenceService;

    public SQLiteRateLimitService(
        IOptions<RateLimitOptions> options,
        IOptions<ConversationPersistenceOptions> persistenceOptions,
        IConversationPersistenceService persistenceService,
        ILogger<SQLiteRateLimitService> logger)
    {
        this.options = options.Value;
        this.persistenceOptions = persistenceOptions.Value;
        this.persistenceService = persistenceService;
        this.logger = logger;

        logger.LogInformation(
            $"SQLiteRateLimitService initialized: " +
            $"{options.Value.MaxMessagesPerMinute}/min, " +
            $"{options.Value.MaxMessagesPerHour}/hour, " +
            $"{options.Value.MaxMessagesPerDay}/day");
    }

    public async Task<RateLimitResult> CheckAndRecordAsync(string conversationId)
    {
        if (!options.Enabled)
            return new RateLimitResult(IsAllowed: true);

        try
        {
            // Ensure database exists (delegates to persistence service)
            await persistenceService.EnsureDatabaseInitializedAsync(conversationId);

            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                DateTime utcNow = DateTime.UtcNow;
                string utcNowIso = utcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                // Step 1: Clean up old requests
                await CleanupOldRequestsAsync(sqliteConnection, sqliteTransaction, utcNow);

                // Step 2: Check time windows
                RateLimitResult? violation = await CheckTimeWindowsAsync(
                    sqliteConnection, sqliteTransaction, utcNow);

                if (violation != null)
                {
                    await sqliteTransaction.RollbackAsync();
                    
                    logger.LogWarning(
                        $"Rate limit DENIED for conversation {conversationId}: {violation.ViolatedLimit}");
                    
                    return violation;
                }

                // Step 3: Record request
                await RecordRequestAsync(sqliteConnection, sqliteTransaction, utcNowIso);

                await sqliteTransaction.CommitAsync();

                logger.LogDebug($"Rate limit ALLOWED for conversation {conversationId}");

                return new RateLimitResult(IsAllowed: true);
            }
            catch
            {
                await sqliteTransaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Rate limit check failed for conversation {conversationId}");
            
            // Fail open - allow request if rate limit service has errors
            // (prevents rate limiter from becoming a single point of failure)
            return new RateLimitResult(IsAllowed: true);
        }
    }

    public async Task ResetAsync(string conversationId)
    {
        try
        {
            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "DELETE FROM rate_limit_log;";

            int rowsDeleted = await sqliteCommand.ExecuteNonQueryAsync();

            logger.LogInformation(
                $"Rate limit reset for conversation {conversationId} ({rowsDeleted} requests cleared)");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to reset rate limit for conversation {conversationId}");
            throw;
        }
    }

    #region Rate Limiting Logic

    /// <summary>
    /// Deletes requests older than 24 hours (our longest time window).
    /// Called before each rate check to keep database size bounded.
    /// </summary>
    private async Task CleanupOldRequestsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTime now)
    {
        DateTime cutoff = now.AddDays(-1);
        string cutoffIso = cutoff.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM rate_limit_log WHERE request_timestamp < @cutoff;";
        command.Parameters.AddWithValue("@cutoff", cutoffIso);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Checks all configured time windows for violations.
    /// Returns the first violated limit or null if all checks pass.
    /// </summary>
    private async Task<RateLimitResult?> CheckTimeWindowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTime utcNow)
    {
        // Check per-minute limit
        if (options.MaxMessagesPerMinute > 0)
        {
            int count = await CountRequestsAsync(
                connection, transaction, utcNow.AddMinutes(-1));

            if (count >= options.MaxMessagesPerMinute)
            {
                return new RateLimitResult(
                    IsAllowed: false,
                    ViolatedLimit: $"MaxMessagesPerMinute ({options.MaxMessagesPerMinute})",
                    RetryAfterSeconds: 60);
            }
        }

        // Check per-hour limit
        if (options.MaxMessagesPerHour > 0)
        {
            int count = await CountRequestsAsync(
                connection, transaction, utcNow.AddHours(-1));

            if (count >= options.MaxMessagesPerHour)
            {
                return new RateLimitResult(
                    IsAllowed: false,
                    ViolatedLimit: $"MaxMessagesPerHour ({options.MaxMessagesPerHour})",
                    RetryAfterSeconds: 3600);
            }
        }

        // Check per-day limit
        if (options.MaxMessagesPerDay > 0)
        {
            int count = await CountRequestsAsync(
                connection, transaction, utcNow.AddDays(-1));

            if (count >= options.MaxMessagesPerDay)
            {
                return new RateLimitResult(
                    IsAllowed: false,
                    ViolatedLimit: $"MaxMessagesPerDay ({options.MaxMessagesPerDay})",
                    RetryAfterSeconds: 86400);
            }
        }

        return null;
    }

    /// <summary>
    /// Counts requests in a time window (from cutoff to now).
    /// </summary>
    private async Task<int> CountRequestsAsync(
        SqliteConnection sqliteConnection,
        SqliteTransaction sqliteTransaction,
        DateTime cutoff)
    {
        string cutoffIso = cutoff.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.Transaction = sqliteTransaction;
        sqliteCommand.CommandText = "SELECT COUNT(*) FROM rate_limit_log WHERE request_timestamp >= @cutoff;";
        sqliteCommand.Parameters.AddWithValue("@cutoff", cutoffIso);

        object? result = await sqliteCommand.ExecuteScalarAsync();
        return result != null ? Convert.ToInt32(result) : 0;
    }

    /// <summary>
    /// Records a new request in the rate limit log.
    /// </summary>
    private async Task RecordRequestAsync(
        SqliteConnection sqliteConnection,
        SqliteTransaction sqliteTransaction,
        string utcNowIso)
    {
        await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.Transaction = sqliteTransaction;
        sqliteCommand.CommandText = "INSERT INTO rate_limit_log (request_timestamp) VALUES (@timestamp);";
        sqliteCommand.Parameters.AddWithValue("@timestamp", utcNowIso);

        await sqliteCommand.ExecuteNonQueryAsync();
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Gets the SQLite connection string for a conversation database.
    /// Reuses the same database file as conversation persistence.
    /// </summary>
    private string GetConnectionString(string conversationId)
    {
        string sqliteDbPath = GetDatabasePath(conversationId);
        return $"Data Source={sqliteDbPath}";
    }

    /// <summary>
    /// Gets the database file path for a conversation.
    /// Same pattern as SQLiteConversationPersistenceService.
    /// </summary>
    private string GetDatabasePath(string conversationId)
    {
        string sanitized = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(persistenceOptions.StoragePath, $"morgana-{sanitized}.db");
    }

    #endregion
}