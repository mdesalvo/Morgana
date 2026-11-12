using Microsoft.Data.Sqlite;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

/// <summary>
/// Limits a conversation's messages per minute, hour and day on sliding windows kept in its own database, failing open.
/// Each check deletes the requests older than a day, counts every configured window and records the request only when all have room,
/// in one transaction.
/// </summary>
public class SQLiteRateLimitService : IRateLimitService
{
    /// <summary>
    /// The format of <c>request_timestamp</c>: fixed-width ISO-8601, so a plain text comparison sorts it in time.
    /// Invariant, because a host locale would change the separators and old rows would stop comparing with new ones.
    /// </summary>
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    /// <summary>
    /// Logs each decision and the fail-open path.
    /// A request admitted because the check could not run would otherwise leave no trace.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>The policy of <c>Morgana:RateLimiting</c>: the switch and the three caps, a cap of 0 disabling its window.</summary>
    private readonly RateLimitOptions options;

    /// <summary>
    /// The owner of the conversation's database, which opens it at the current schema.
    /// The rate check runs before any agent has had the chance to create the database.
    /// </summary>
    private readonly SQLiteConversationPersistenceService persistenceService;

    /// <summary>Binds the limiter to its policy and to the conversation's database.</summary>
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

    /// <summary>Admits the request and records it when every configured window has room, failing open when the check cannot run.</summary>
    /// <param name="conversationId">The conversation whose windows are counted, which also selects the database.</param>
    /// <returns>An admission or a refusal naming the narrowest full window and its retry-after.</returns>
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

            // Cleanup, count and record share one transaction: two requests of one conversation checked at the same time
            // could otherwise both slip past the same cap.
            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                // One instant for the whole check, so the three windows are measured from the same point
                // and a request cannot fall inside one of them but outside another.
                DateTime utcNow = DateTime.UtcNow;

                // Yesterday's requests can no longer breach any window, so they go before they are
                // counted: the log stays bounded without a housekeeping pass of its own.
                await CleanupOldRequestsAsync(sqliteConnection, sqliteTransaction, utcNow);

                // The narrowest window with no room left; nothing when all three still have some.
                RateLimitResult? violation = await CheckTimeWindowsAsync(sqliteConnection, sqliteTransaction, utcNow);

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
                await RecordRequestAsync(sqliteConnection, sqliteTransaction, utcNow);

                // Count and record become visible together: until this line no concurrent check sees this
                // request, which is what stops two of them slipping past one cap.
                await sqliteTransaction.CommitAsync();

                logger.LogDebug("Rate limit ALLOWED for conversation {ConversationId}", conversationId);

                // The request goes on to the dust gate and then to the conversation.
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

            // The limiter fails open, so it never becomes the one fault that stops every conversation.
            return new RateLimitResult(IsAllowed: true);
        }
    }

    #region Rate Limiting Logic

    /// <summary>Deletes the requests older than a day, which no window can count any longer.</summary>
    /// <param name="connection">Open connection to the conversation database.</param>
    /// <param name="transaction">The enclosing check-and-record transaction this delete joins.</param>
    /// <param name="now">Reference instant; everything older than 24h before it is deleted.</param>
    private async Task CleanupOldRequestsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTime now)
    {
        // A day back, which is the widest window: anything older cannot affect a count.
        string cutoffIso = now.AddDays(-1).ToString(TimestampFormat, CultureInfo.InvariantCulture);

        // Inside the caller's transaction, so a request denied further down un-deletes these rows too.
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM rate_limit_log WHERE request_timestamp < @cutoff;";
        command.Parameters.AddWithValue("@cutoff", cutoffIso);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Finds the narrowest configured window that has no room left for this request; null when every one has room.</summary>
    /// <param name="connection">Open connection to the conversation database.</param>
    /// <param name="transaction">The enclosing check-and-record transaction the counts are read in.</param>
    /// <param name="utcNow">Reference instant each window is measured back from.</param>
    private async Task<RateLimitResult?> CheckTimeWindowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTime utcNow)
    {
        // The three windows narrowest first, so a refusal names the one the caller can wait out soonest.
        (RateLimitWindow Window, int Cap, TimeSpan Span)[] windows =
        [
            (RateLimitWindow.PerMinute, options.MaxMessagesPerMinute, TimeSpan.FromMinutes(1)),
            (RateLimitWindow.PerHour, options.MaxMessagesPerHour, TimeSpan.FromHours(1)),
            (RateLimitWindow.PerDay, options.MaxMessagesPerDay, TimeSpan.FromDays(1))
        ];

        foreach ((RateLimitWindow window, int cap, TimeSpan span) in windows)
        {
            // A cap of 0 switches its window off rather than allowing zero requests.
            if (cap <= 0)
                continue;

            // The requests already admitted in this window.
            int requestCount = await CountRequestsAsync(connection, transaction, utcNow - span);

            // This request would be one more, so a window already at its cap refuses it; the window empties within its own span.
            if (requestCount >= cap)
                return new RateLimitResult(
                    IsAllowed: false,
                    ViolatedWindow: window,
                    ViolatedCap: cap,
                    RetryAfterSeconds: (int)span.TotalSeconds);
        }

        // Every configured window has room.
        return null;
    }

    /// <summary>Counts the requests already admitted since the cutoff.</summary>
    /// <param name="sqliteConnection">Open connection to the conversation database.</param>
    /// <param name="sqliteTransaction">The enclosing check-and-record transaction this count is read in.</param>
    /// <param name="cutoff">Lower bound of the window; rows at or after it are counted.</param>
    private async Task<int> CountRequestsAsync(
        SqliteConnection sqliteConnection,
        SqliteTransaction sqliteTransaction,
        DateTime cutoff)
    {
        // Compared as text, which the fixed-width format sorts in time.
        string cutoffIso = cutoff.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        // Read inside the transaction, so a concurrent check cannot count between this read and the record.
        await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.Transaction = sqliteTransaction;
        sqliteCommand.CommandText = "SELECT COUNT(*) FROM rate_limit_log WHERE request_timestamp >= @cutoff;";
        sqliteCommand.Parameters.AddWithValue("@cutoff", cutoffIso);

        // An empty answer counts as no request.
        object? requestCount = await sqliteCommand.ExecuteScalarAsync();
        return requestCount != null ? Convert.ToInt32(requestCount, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>Records the admitted request in the log that the windows count.</summary>
    /// <param name="sqliteConnection">Open connection to the conversation database.</param>
    /// <param name="sqliteTransaction">The enclosing check-and-record transaction, committed by the caller.</param>
    /// <param name="utcNow">The instant of the request, the same the windows were measured from.</param>
    private async Task RecordRequestAsync(
        SqliteConnection sqliteConnection,
        SqliteTransaction sqliteTransaction,
        DateTime utcNow)
    {
        // Written inside the transaction, so it becomes visible together with the counts that admitted it.
        await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.Transaction = sqliteTransaction;
        sqliteCommand.CommandText = "INSERT INTO rate_limit_log (request_timestamp) VALUES (@timestamp);";
        sqliteCommand.Parameters.AddWithValue("@timestamp", utcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture));

        await sqliteCommand.ExecuteNonQueryAsync();
    }

    #endregion
}