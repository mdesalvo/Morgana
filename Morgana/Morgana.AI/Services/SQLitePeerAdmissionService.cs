using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

/// <summary>
/// Counts the conversations each partner opens over a sliding hour, in a database of its own beside the conversations.
/// The one ledger that is not a conversation's, since a partner spans every conversation it opens; it is also this instance's alone,
/// so a deployment running several instances reads the configured limit as per instance.
/// </summary>
public class SQLitePeerAdmissionService : IPeerAdmissionService
{
    /// <summary>Name of the ledger, kept apart from every <c>morgana-{conversation}.sqlite</c> beside it.</summary>
    private const string DatabaseFileName = "morgana-peers.sqlite";

    /// <summary>Length of the window a system's openings are counted over.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>Where the ledger sits: the same directory the conversations themselves are kept in.</summary>
    private readonly ConversationPersistenceOptions persistenceOptions;

    /// <summary>Allowance of each partner, resolved once from what configuration declared for it.</summary>
    private readonly Dictionary<string, int> limitByIssuer;

    /// <summary>What each partner is told when it has opened all it may, as its own entry words it.</summary>
    private readonly Dictionary<string, string> refusalMessageByIssuer;

    /// <summary>Records a system turned away, whether for going too far or because the count could not be read.</summary>
    private readonly ILogger logger;

    /// <summary>Reads what each admitted partner is allowed, off the same entry that gave it its reach.</summary>
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
        // This installation's own agents are not on this list at all: they join the conversation the
        // user is already having rather than opening one, so there is nothing here to count.
        List<Records.PartnerOptions> cappedPartners =
            [.. ConfigurationAgentDirectoryService.ResolveAdmittedPartners(configuration)
                    .Select(admitted => admitted.Partner)
                    // A ceiling switched off is a deployment saying that partner opens what it likes and
                    // one with no number bounds nothing. Startup refuses the latter, so what is tolerated
                    // here is a service built outside a validated deployment: never a limit invented for it.
                    .Where(partner => partner.InboundPolicy!.RateLimiting is { Enabled: true, MaxConversationsPerHour: > 0 })];

        // Each partner is keyed by the issuer its tokens carry, its name when it declares none.
        limitByIssuer = cappedPartners.ToDictionary(
            partner => (partner.InboundPolicy!.Issuer ?? partner.Name).Trim(),
            partner => partner.InboundPolicy!.RateLimiting!.MaxConversationsPerHour!.Value,
            StringComparer.OrdinalIgnoreCase);

        refusalMessageByIssuer = cappedPartners.ToDictionary(
            partner => (partner.InboundPolicy!.Issuer ?? partner.Name).Trim(),
            partner => partner.InboundPolicy!.RateLimiting!.ErrorMessagePerHour,
            StringComparer.OrdinalIgnoreCase);

        // What an operator reads to know which partners are actually held to something, a ceiling
        // nobody wrote being indistinguishable at runtime from one nobody needed.
        logger.LogInformation(
            "SQLitePeerAdmissionService initialized: {Count} partner(s) held to a limit on new conversations per hour", limitByIssuer.Count);
    }

    /// <inheritdoc/>
    public async Task<Records.PeerAdmissionResult> TryAdmitNewConversationAsync(string issuer)
    {
        // A partner nobody put a ceiling on opens what it likes. It still had to prove who it is and be
        // admitted to this agent: what is absent here is a ceiling, never the gate.
        int conversationLimit = limitByIssuer.GetValueOrDefault(issuer, 0);
        if (conversationLimit <= 0)
            return new Records.PeerAdmissionResult(IsAdmitted: true);

        try
        {
            // The ledger beside the conversations, created on its first use.
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

            // The conversations this partner has opened within the window.
            await using SqliteCommand countCommand = connection.CreateCommand();
            countCommand.Transaction = transaction;
            countCommand.CommandText = "SELECT COUNT(*) FROM peer_conversation_log WHERE issuer = $issuer;";
            countCommand.Parameters.AddWithValue("$issuer", issuer);
            long openedConversations = Convert.ToInt64(await countCommand.ExecuteScalarAsync(), CultureInfo.InvariantCulture);

            // A refusal leaves no trace: counting it would push the system further past its limit on
            // every retry, so being turned away would lengthen the wait it caused.
            if (openedConversations >= conversationLimit)
            {
                await transaction.RollbackAsync();

                logger.LogWarning(
                    "Partner '{Issuer}' has opened its {Limit} conversation(s) for this hour and is admitted to no further ones until the window moves",
                    issuer, conversationLimit);

                return new Records.PeerAdmissionResult(IsAdmitted: false, RefusalMessage: refusalMessageByIssuer.GetValueOrDefault(issuer));
            }

            // Admitted, so this opening counts against the partner's next ones.
            await ExecuteAsync(
                connection, transaction,
                "INSERT INTO peer_conversation_log (issuer, opened_at) VALUES ($issuer, $openedAt);",
                ("$issuer", issuer), ("$openedAt", ToIso(utcNow)));

            // Count and record become visible together, which is what stops two requests slipping
            // past one limit.
            await transaction.CommitAsync();

            // The partner may open the conversation.
            return new Records.PeerAdmissionResult(IsAdmitted: true);
        }
        catch (Exception ex)
        {
            // The one limiter that closes on its own failure, because of where it stands: behind this door a request reaches an agent
            // with none of the guard, classifier and channel rate limit of a user's path, so this count is all that bounds a partner.
            // Admitted while unreadable, the only measure would be silently gone.
            logger.LogError(ex, "Could not weigh the conversations '{Issuer}' has opened; it is turned away", issuer);

            // Answered in the words a partner over its allowance would read, its own entry having
            // nothing to say about a ledger this installation could not open.
            return new PeerAdmissionResult(IsAdmitted: false, RefusalMessage: null);
        }
    }

    /// <summary>Creates the ledger on first use, there being no conversation whose schema carries it.</summary>
    /// <param name="connection">Open connection to the ledger.</param>
    private static async Task EnsureLedgerAsync(SqliteConnection connection)
    {
        // Creating what already exists changes nothing, so every check may run it.
        await using SqliteCommand command = connection.CreateCommand();

        // Indexed by issuer because every question asked of this table is about one partner. Indexed by
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
        // The statement joins the check's transaction.
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Renders an instant so that stored openings sort and compare as text, with the same separators and calendar on every host.</summary>
    /// <param name="instant">Instant to render.</param>
    private static string ToIso(DateTime instant)
        // Fixed-width ISO-8601, invariant.
        => instant.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>Where the ledger file sits, beside the conversations of this installation.</summary>
    private string ResolveDatabasePath()
    {
        // The directory may not exist yet on an installation that has no conversation so far.
        Directory.CreateDirectory(persistenceOptions.StoragePath);

        // The fixed file name keeps it apart from the conversations' databases.
        return Path.Combine(persistenceOptions.StoragePath, DatabaseFileName);
    }
}