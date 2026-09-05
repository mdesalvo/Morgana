using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IPeerAdmissionService"/>: a sliding hour of conversation openings per admitted
/// system, in a database of its own beside the conversations.
/// </summary>
/// <remarks>
/// The one ledger in Morgana that is not a conversation's. It is not one because the thing it
/// measures is not either: an issuer spans every conversation it opens, so a count kept inside any
/// of them could never see the others. It stays a ledger of this instance — a deployment running
/// several must read the configured limit as being per instance, since nothing here is shared
/// between them.
/// </remarks>
public class SQLitePeerAdmissionService : IPeerAdmissionService
{
    /// <summary>Name of the ledger, kept apart from every <c>morgana-{conversation}.db</c> beside it.</summary>
    private const string DatabaseFileName = "morgana-peers.db";

    /// <summary>Length of the window a system's openings are counted over.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Where the ledger sits: the same directory the conversations themselves are kept in.</summary>
    private readonly ConversationPersistenceOptions persistenceOptions;

    /// <summary>Allowance of each system, resolved once from what configuration declared for it.</summary>
    private readonly Dictionary<string, int> limitByIssuer;

    /// <summary>Records a system turned away and the fail-open path, which leaves no other trace.</summary>
    private readonly ILogger logger;

    /// <summary>Reads what each admitted system is allowed, off the same entry that gave it its reach.</summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="persistenceOptions">Persistence configuration, read for the directory the ledger lives in.</param>
    /// <param name="logger">Logger for refusals and for a count that could not be read.</param>
    public SQLitePeerAdmissionService(
        IConfiguration configuration,
        IOptions<ConversationPersistenceOptions> persistenceOptions,
        ILogger<SQLitePeerAdmissionService> logger)
    {
        this.persistenceOptions = persistenceOptions.Value;
        this.logger = logger;

        // The allowance is declared where the reach is and nowhere else: how far a partner may go and
        // how often it may come back are one declaration about one partner, so there is no second
        // place to look and no default quietly deciding for a partner nobody wrote a number for.
        limitByIssuer = ConfigurationAgentDirectoryService.ResolveInboundSystems(configuration)
            .Where(system => !string.IsNullOrWhiteSpace(system.Issuer))

            // This installation's own ring is left out. A colleague of ours joins the conversation the
            // user is already having rather than opening one, so it would never be counted anyway —
            // stated here so that a local consultation reaching an unopened conversation, which is a
            // fault of ours rather than a partner's traffic, is not refused as if it were.
            .Where(system => !string.Equals(system.Issuer.Trim(), Constants.Morgana, StringComparison.OrdinalIgnoreCase))
            .GroupBy(system => system.Issuer.Trim(), StringComparer.OrdinalIgnoreCase)

            // An entry that names no number is kept out of the map rather than given one. Startup
            // refuses such an entry for every partner, so what this tolerates is a directory built
            // outside a validated deployment: a ceiling is missing here, never quietly invented.
            .Select(entries => (Issuer: entries.Key, Limit: entries.Select(system => system.MaxConversationsPerHour).FirstOrDefault(limit => limit is not null)))
            .Where(system => system.Limit is not null)
            .ToDictionary(system => system.Issuer, system => system.Limit!.Value, StringComparer.OrdinalIgnoreCase);

        // What an operator reads to know which partners are actually held to something, a ceiling
        // nobody wrote being indistinguishable at runtime from one nobody needed.
        logger.LogInformation(
            "SQLitePeerAdmissionService initialized: {Count} system(s) held to a limit on new conversations per hour", limitByIssuer.Count);
    }

    /// <inheritdoc/>
    public async Task<bool> TryAdmitNewConversationAsync(string issuer)
    {
        // A system nobody put a limit on opens what it likes. It still had to prove who it is and be
        // admitted to this agent: what is absent here is a ceiling, never the gate.
        int limit = limitByIssuer.GetValueOrDefault(issuer, 0);
        if (limit <= 0)
            return true;

        try
        {
            await using SqliteConnection connection = new SqliteConnection($"Data Source={ResolveDatabasePath()}");
            await connection.OpenAsync();

            await EnsureLedgerAsync(connection);

            // Counting and recording are one act: two requests weighed at once would otherwise each
            // find room the other was about to take. Both would be admitted past the same limit.
            await using SqliteTransaction transaction = connection.BeginTransaction();

            // One instant for the whole check, so what is counted and what is written agree on when
            // the window opened.
            DateTime utcNow = DateTime.UtcNow;

            // Openings older than the window can no longer refuse anybody, so they go before they are
            // counted: the ledger stays bounded with no housekeeping pass of its own.
            await ExecuteAsync(
                connection, transaction,
                "DELETE FROM peer_conversation_log WHERE opened_at < $horizon;",
                ("$horizon", ToIso(utcNow - Window)));

            await using SqliteCommand countCommand = connection.CreateCommand();
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM peer_conversation_log WHERE issuer = $issuer;";
            countCommand.Parameters.AddWithValue("$issuer", issuer);

            long opened = Convert.ToInt64(await countCommand.ExecuteScalarAsync());

            // A refusal leaves no trace: counting it would push the system further past its limit on
            // every retry, so being turned away would lengthen the wait it caused.
            if (opened >= limit)
            {
                await transaction.RollbackAsync();

                logger.LogWarning(
                    "System '{Issuer}' has opened its {Limit} conversation(s) for this hour and is admitted to no further ones until the window moves",
                    issuer, limit);

                return false;
            }

            await ExecuteAsync(
                connection, transaction,
                "INSERT INTO peer_conversation_log (issuer, opened_at) VALUES ($issuer, $openedAt);",
                ("$issuer", issuer), ("$openedAt", ToIso(utcNow)));

            // Count and record become visible together, which is what stops two requests slipping
            // past one limit.
            await transaction.CommitAsync();

            return true;
        }
        catch (Exception ex)
        {
            // Fails open, as every limiter here does: a partner is refused for going too far, never
            // because this installation could not read its own count.
            logger.LogError(ex, "Could not weigh the conversations '{Issuer}' has opened; it is admitted", issuer);
            return true;
        }
    }

    /// <summary>Creates the ledger on first use, there being no conversation whose schema carries it.</summary>
    /// <param name="connection">Open connection to the ledger.</param>
    private static async Task EnsureLedgerAsync(SqliteConnection connection)
    {
        await using SqliteCommand command = connection.CreateCommand();

        // Indexed by issuer because every question asked of this table is about one system. Indexed by
        // the instant because the window is what decides which of its rows still count.
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS peer_conversation_log (
                issuer TEXT NOT NULL,
                opened_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_peer_conversation_log ON peer_conversation_log (issuer, opened_at);
            """;

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs one statement of the check inside the transaction that makes them one act.</summary>
    /// <param name="connection">Open connection to the ledger.</param>
    /// <param name="transaction">Transaction the whole check runs in.</param>
    /// <param name="sql">Statement to run.</param>
    /// <param name="parameters">Values it binds.</param>
    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Renders an instant so that stored openings sort and compare as text.</summary>
    /// <param name="instant">Instant to render.</param>
    private static string ToIso(DateTime instant)
        => instant.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>Where the ledger file sits, beside the conversations of this installation.</summary>
    private string ResolveDatabasePath()
    {
        Directory.CreateDirectory(persistenceOptions.StoragePath);

        return Path.Combine(persistenceOptions.StoragePath, DatabaseFileName);
    }
}