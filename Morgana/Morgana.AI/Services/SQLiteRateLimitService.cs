using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

/// <summary>
/// SQLite-based sliding-window rate limiter: per-minute/hour/day caps, one row per accepted
/// request in the SAME per-conversation database conversation persistence uses (table
/// <c>rate_limit_log</c>). Each check cleans up requests older than 24h, counts each configured
/// window, and — only if every window is under its cap — records the request, all in one transaction.
/// </summary>
public class SQLiteRateLimitService : IRateLimitService
{
    /// <summary>
    /// Logger for allow/deny decisions and for the fail-open path, which admits a request the
    /// check could not evaluate and would otherwise leave no trace.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Rate limit policy from <c>Morgana:RateLimiting</c>: enable flag, the three window caps
    /// (a cap of 0 disables its window) and the error message templates.
    /// </summary>
    private readonly RateLimitOptions options;

    /// <summary>
    /// Persistence configuration, read only for <c>StoragePath</c>: <c>rate_limit_log</c> lives in
    /// the conversation's own database, not in one of its own.
    /// </summary>
    private readonly ConversationPersistenceOptions persistenceOptions;

    /// <summary>
    /// Owner of the database file. Delegated to for schema creation, since the rate check runs
    /// before any agent has had the chance to create the conversation's database.
    /// </summary>
    private readonly IConversationPersistenceService persistenceService;

    /// <param name="options">Rate limit configuration (<c>Morgana:RateLimiting</c>): enable flag, the three window caps and the error message templates.</param>
    /// <param name="persistenceOptions">Persistence configuration, read only for <c>StoragePath</c> — the log lives in the same per-conversation database.</param>
    /// <param name="persistenceService">Owner of the database file; delegated to for schema creation, since a rate check can run before any agent has saved a session.</param>
    /// <param name="logger">Logger for allow/deny decisions and fail-open diagnostics.</param>
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

    /// <summary>
    /// Evaluates the per-minute/hour/day sliding windows for the given conversation and,
    /// if all limits are respected, records the current request. Fails open on error.
    /// </summary>
    /// <param name="conversationId">Conversation whose windows are evaluated; also selects the database file.</param>
    /// <returns>
    /// An allowing result when every configured window is under its cap (the request has then been
    /// recorded), otherwise a denying result carrying the first violated limit and its retry-after.
    /// Also allowing when rate limiting is disabled, or when the check itself failed — the limiter
    /// fails open rather than becoming a single point of failure.
    /// </returns>
    public async Task<RateLimitResult> CheckAndRecordAsync(string conversationId)
    {
        if (!options.Enabled)
            return new RateLimitResult(IsAllowed: true);

        try
        {
            // Rate limiting runs BEFORE any agent executes, so the database may not exist yet —
            // it's normally created lazily the first time an agent saves its session. Calling this
            // here too, on-demand, avoids a FileNotFoundException on a brand-new conversation.
            await persistenceService.EnsureDatabaseInitializedAsync(conversationId);

            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // Cleanup + count + record all share one transaction so a second request for the same
            // conversation arriving mid-check can't interleave with this one and both slip past
            // the same limit — the whole check-and-record sequence is what needs to be atomic.
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
                        "Rate limit DENIED for conversation {ConversationId}: {ViolationViolatedLimit}", conversationId, violation.ViolatedLimit);

                    return violation;
                }

                // Step 3: Record request
                await RecordRequestAsync(sqliteConnection, sqliteTransaction, utcNowIso);

                await sqliteTransaction.CommitAsync();

                logger.LogDebug("Rate limit ALLOWED for conversation {ConversationId}", conversationId);

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
            logger.LogError(ex, "Rate limit check failed for conversation {ConversationId}", conversationId);

            // Fail open - allow request if rate limit service has errors
            // (prevents rate limiter from becoming a single point of failure)
            return new RateLimitResult(IsAllowed: true);
        }
    }

    /// <summary>
    /// Clears all recorded request timestamps for the given conversation, effectively
    /// resetting its rate limit windows.
    /// </summary>
    /// <param name="conversationId">Conversation whose rate limit log is cleared.</param>
    /// <exception cref="Exception">
    /// Rethrown from the underlying SQLite access after logging: unlike
    /// <see cref="CheckAndRecordAsync"/>, an explicit reset that silently did nothing would be
    /// worse than a visible failure, so this path does not fail open.
    /// </exception>
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
                "Rate limit reset for conversation {ConversationId} ({RowsDeleted} requests cleared)", conversationId, rowsDeleted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset rate limit for conversation {ConversationId}", conversationId);
            throw;
        }
    }

    #region Rate Limiting Logic

    /// <summary>
    /// Deletes requests older than 24 hours (our longest time window).
    /// Called before each rate check to keep database size bounded.
    /// </summary>
    /// <param name="connection">Open connection to the conversation database.</param>
    /// <param name="transaction">The enclosing check-and-record transaction this delete joins.</param>
    /// <param name="now">Reference instant; everything older than 24h before it is deleted.</param>
    private async Task CleanupOldRequestsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTime now)
    {
        // ISO-8601 with a fixed-width, zero-padded format: request_timestamp is stored as TEXT,
        // and this specific format sorts correctly under a plain lexicographic "<"/">=" comparison
        // — the same trick every timestamp comparison in this file relies on.
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
    /// <param name="connection">Open connection to the conversation database.</param>
    /// <param name="transaction">The enclosing check-and-record transaction the counts are read in.</param>
    /// <param name="utcNow">Reference instant each window is measured back from.</param>
    /// <returns>
    /// A denying <see cref="RateLimitResult"/> for the first window found over its cap — narrowest
    /// first (minute, then hour, then day) — or <c>null</c> when every configured window has room.
    /// </returns>
    private async Task<RateLimitResult?> CheckTimeWindowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTime utcNow)
    {
        // >= not >: count is the number of PRIOR requests already in the window, and this one
        // would be the (count+1)th — so count==limit means this request is the one that breaches
        // the cap and must be denied, not admitted as the "last allowed" one. A limit of 0 skips
        // the window entirely (see the `> 0` guards) rather than meaning "zero requests allowed".
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
    /// <param name="sqliteConnection">Open connection to the conversation database.</param>
    /// <param name="sqliteTransaction">The enclosing check-and-record transaction this count is read in.</param>
    /// <param name="cutoff">Lower bound of the window; rows at or after it are counted.</param>
    /// <returns>Number of requests already recorded in the window, 0 if the query yields nothing.</returns>
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
    /// <param name="sqliteConnection">Open connection to the conversation database.</param>
    /// <param name="sqliteTransaction">The enclosing check-and-record transaction, committed by the caller.</param>
    /// <param name="utcNowIso">Request instant, in the fixed-width ISO-8601 format every comparison in this file sorts on.</param>
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
    /// <param name="conversationId">Conversation whose database is addressed.</param>
    /// <returns>Connection string pointing at that conversation's SQLite file.</returns>
    private string GetConnectionString(string conversationId)
    {
        string sqliteDbPath = GetDatabasePath(conversationId);
        return $"Data Source={sqliteDbPath}";
    }

    /// <summary>
    /// Gets the database file path for a conversation.
    /// Same pattern as SQLiteConversationPersistenceService.
    /// </summary>
    /// <param name="conversationId">Client-supplied conversation id; sanitised here before it touches a path.</param>
    /// <returns><c>{StoragePath}/morgana-{sanitizedId}.db</c> — the same physical file persistence resolves for that id.</returns>
    private string GetDatabasePath(string conversationId)
    {
        // conversationId is client-supplied (arrives via the REST API), so it's split on every
        // character the OS forbids in a file name and rejoined with "_" before it ever touches a
        // path — the same sanitisation SQLiteConversationPersistenceService applies to the exact
        // same id, so both services always agree on which physical file backs a given conversation.
        string sanitized = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(persistenceOptions.StoragePath, $"morgana-{sanitized}.db");
    }

    #endregion
}