using Microsoft.Data.Sqlite;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

/// <summary>
/// SQLite-based sliding-window rate limiter: per-minute/hour/day caps, one row per accepted
/// request in the conversation's own database (table <c>rate_limit_log</c>), which it opens
/// through <see cref="SQLiteConversationPersistenceService"/>. Each check cleans up requests older
/// than 24h, counts each configured window and records the request only if every window is under
/// its cap, all in one transaction.
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
    /// Owner of the database file. Opens it at the current schema, since the rate check runs
    /// before any agent has had the chance to create the conversation's database.
    /// </summary>
    private readonly SQLiteConversationPersistenceService persistenceService;

    /// <param name="options">Rate limit configuration (<c>Morgana:RateLimiting</c>): enable flag, the three window caps and the error message templates.</param>
    /// <param name="persistenceService">Owner of the database file: this service keeps its table in the conversation's own database and opens it through the persistence service that owns it, since a rate check can run before any agent has saved a session.</param>
    /// <param name="logger">Logger for allow/deny decisions and fail-open diagnostics.</param>
    public SQLiteRateLimitService(
        IOptions<RateLimitOptions> options,
        SQLiteConversationPersistenceService persistenceService,
        ILogger<SQLiteRateLimitService> logger)
    {
        this.options = options.Value;
        this.persistenceService = persistenceService;
        this.logger = logger;

        // The caps this installation runs under, on one line at startup: they are read per request from
        // here on, so this is where an operator confirms the deployment took the configuration it meant.
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
        // Switched off, so every request passes and none is recorded: a deployment that does not limit
        // pays nothing, not even the write.
        if (!options.Enabled)
            return new RateLimitResult(IsAllowed: true);

        try
        {
            // Rate limiting runs before any agent executes, so the database may not exist yet; opening it creates it.
            // It is also what scopes the limit: a caller opening a second conversation is counted separately, by design.
            await using SqliteConnection sqliteConnection = await persistenceService.OpenDatabaseAsync(conversationId);

            // Cleanup + count + record all share one transaction so a second request for the same
            // conversation arriving mid-check can't interleave with this one and both slip past
            // the same limit — the whole check-and-record sequence is what needs to be atomic.
            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                // One instant for the whole check, so the three windows are measured from the same point
                // and a request cannot fall inside one of them but outside another.
                DateTime utcNow = DateTime.UtcNow;
                string utcNowIso = utcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

                // Yesterday's requests can no longer breach any window, so they go before they are
                // counted: the log stays bounded without a housekeeping pass of its own.
                await CleanupOldRequestsAsync(sqliteConnection, sqliteTransaction, utcNow);

                // The narrowest window with no room left, or nothing when all three still have some.
                RateLimitResult? violation = await CheckTimeWindowsAsync(
                    sqliteConnection, sqliteTransaction, utcNow);

                // A denied request leaves no trace: counting it would push the caller further past the
                // cap on every retry, so being refused would lengthen the wait it caused.
                if (violation != null)
                {
                    await sqliteTransaction.RollbackAsync();

                    logger.LogWarning(
                        "Rate limit DENIED for conversation {ConversationId}: {ViolationViolatedLimit}", conversationId, violation.ViolatedLimit);

                    return violation;
                }

                // Admitted, so it counts against the next caller's windows.
                await RecordRequestAsync(sqliteConnection, sqliteTransaction, utcNowIso);

                // Count and record become visible together: until this line no concurrent check sees this
                // request, which is what stops two of them slipping past one cap.
                await sqliteTransaction.CommitAsync();

                logger.LogDebug("Rate limit ALLOWED for conversation {ConversationId}", conversationId);

                return new RateLimitResult(IsAllowed: true);
            }
            catch
            {
                // Nothing half-written: a failure here must not leave a request counted against a caller
                // whose turn never ran.
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
            // The reset addresses the conversation's own database, which already exists for any conversation that was limited.
            await using SqliteConnection sqliteConnection = await persistenceService.OpenDatabaseAsync(conversationId);

            // The whole log, not a window of it: this hands the conversation back its full allowance.
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "DELETE FROM rate_limit_log;";

            int rowsDeleted = await sqliteCommand.ExecuteNonQueryAsync();

            // How much allowance came back, which is the only evidence the reset did anything.
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
        // ISO-8601 with a fixed-width, zero-padded format: request_timestamp is stored as TEXT.
        // This specific format sorts correctly under a plain lexicographic "<"/">=" comparison
        // — the same trick every timestamp comparison in this file relies on. Invariant, because a
        // host locale changes the separators or the calendar and old rows would stop comparing with new ones.
        // A day back, which outlives the widest configured window: anything older cannot affect a count.
        DateTime cutoff = now.AddDays(-1);
        string cutoffIso = cutoff.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        // Inside the caller's transaction, so a request denied further down un-deletes these rows too.
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
    /// A denying <see cref="RateLimitResult"/> for the first window found over its cap, narrowest
    /// first (minute, then hour, then day); <c>null</c> when every configured window has room.
    /// </returns>
    private async Task<RateLimitResult?> CheckTimeWindowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTime utcNow)
    {
        // >= not >: count is the number of PRIOR requests already in the window and this one
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
                    ViolatedWindow: RateLimitWindow.PerMinute,
                    ViolatedCap: options.MaxMessagesPerMinute,
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
                    ViolatedWindow: RateLimitWindow.PerHour,
                    ViolatedCap: options.MaxMessagesPerHour,
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
                    ViolatedWindow: RateLimitWindow.PerDay,
                    ViolatedCap: options.MaxMessagesPerDay,
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
        string cutoffIso = cutoff.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.Transaction = sqliteTransaction;
        sqliteCommand.CommandText = "SELECT COUNT(*) FROM rate_limit_log WHERE request_timestamp >= @cutoff;";
        sqliteCommand.Parameters.AddWithValue("@cutoff", cutoffIso);

        object? result = await sqliteCommand.ExecuteScalarAsync();
        return result != null ? Convert.ToInt32(result, CultureInfo.InvariantCulture) : 0;
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
}