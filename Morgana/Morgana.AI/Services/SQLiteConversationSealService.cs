using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

/// <summary>
/// SQLite-backed seal in the single-row <c>conversation_seal</c> table of the conversation's own database.
/// Every question fails closed: a seal that cannot be read admits nobody.
/// </summary>
/// <remarks>
/// The seal is 80 random bits in Crockford base32, shown as <c>XXXX-XXXX-XXXX-XXXX</c> so a person can
/// copy it by hand. Only the SHA-256 of its normalised form is kept: at 80 random bits a slow key
/// derivation buys nothing and the seal is never read back, only compared.
/// </remarks>
public class SQLiteConversationSealService : IConversationSealService
{
    /// <summary>Crockford's base32 alphabet: no I, L, O or U, so no two symbols are mistaken for each other.</summary>
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Random bytes behind one seal: 80 bits, exactly sixteen base32 symbols.</summary>
    private const int SealEntropyBytes = 10;

    /// <summary>Symbols between two dashes of the displayed seal.</summary>
    private const int SealGroupLength = 4;

    /// <summary>Records refused verifications by conversation id alone, never by the seal presented.</summary>
    private readonly ILogger logger;

    /// <summary>Persistence configuration, read only for <c>StoragePath</c>: the seal lives in the conversation's own database.</summary>
    private readonly ConversationPersistenceOptions persistenceOptions;

    /// <summary>Owner of the database file and of its schema, which the seal table is part of.</summary>
    private readonly IConversationPersistenceService persistenceService;

    /// <summary>Initializes the seal service over the per-conversation databases the persistence service owns.</summary>
    public SQLiteConversationSealService(
        IOptions<ConversationPersistenceOptions> persistenceOptions,
        IConversationPersistenceService persistenceService,
        ILogger<SQLiteConversationSealService> logger)
    {
        this.persistenceOptions = persistenceOptions.Value;
        this.persistenceService = persistenceService;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<string?> SealAsync(string conversationId, string issuer)
    {
        string seal = GenerateSeal();

        // Creates the database of a conversation being opened: from here it exists on record, sealed
        await persistenceService.EnsureDatabaseInitializedAsync(conversationId);

        await using SqliteConnection connection = new SqliteConnection(GetConnectionString(conversationId));
        await connection.OpenAsync();

        // One statement is the whole gate: a second start on the same id finds the row taken, a conversation
        // older than the seal finds its handshake already settled. Either way nothing is written.
        await using SqliteCommand sealCommand = connection.CreateCommand();
        sealCommand.CommandText =
"""
INSERT OR IGNORE INTO conversation_seal (id, seal_hash, issuer, sealed_at)
SELECT 1, @seal_hash, @issuer, @sealed_at
WHERE NOT EXISTS (SELECT 1 FROM channel_metadata);
""";
        sealCommand.Parameters.AddWithValue("@seal_hash", HashSeal(seal));
        sealCommand.Parameters.AddWithValue("@issuer", issuer);
        sealCommand.Parameters.AddWithValue("@sealed_at", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));

        return await sealCommand.ExecuteNonQueryAsync() == 1 ? seal : null;
    }

    /// <inheritdoc/>
    public async Task<bool> VerifyAsync(string conversationId, string issuer, string? presentedSeal)
    {
        // Asking about an id nobody started must not create its database
        if (string.IsNullOrWhiteSpace(presentedSeal) || !persistenceService.ConversationExists(conversationId))
            return false;

        try
        {
            // A database older than the seal is brought to the current schema, where its seal table is empty
            await persistenceService.EnsureDatabaseInitializedAsync(conversationId);

            await using SqliteConnection connection = new SqliteConnection(GetConnectionString(conversationId));
            await connection.OpenAsync();

            await using SqliteCommand readCommand = connection.CreateCommand();
            readCommand.CommandText = "SELECT seal_hash, issuer FROM conversation_seal WHERE id = 1;";
            await using SqliteDataReader reader = await readCommand.ExecuteReaderAsync();

            // No row is a conversation sealed by no one: nobody holds a seal that could open it
            if (!await reader.ReadAsync())
            {
                logger.LogWarning("Conversation {ConversationId} carries no seal; refusing", conversationId);
                return false;
            }

            // The channel that opened the conversation is the only one that can draw it: another issuer is
            // refused even holding the right seal
            byte[] storedHash = reader.GetFieldValue<byte[]>(0);
            string sealingIssuer = reader.GetString(1);
            bool admitted = string.Equals(sealingIssuer, issuer, StringComparison.Ordinal)
                            && CryptographicOperations.FixedTimeEquals(storedHash, HashSeal(presentedSeal));
            if (!admitted)
                logger.LogWarning("Seal refused for conversation {ConversationId}", conversationId);
            return admitted;
        }
        catch (Exception ex)
        {
            // A seal that cannot be checked opens nothing: the user retries, a thief gets no window
            logger.LogError(ex, "Seal verification failed for {ConversationId}; refusing", conversationId);
            return false;
        }
    }

    /// <summary>Draws a fresh seal in its displayed form.</summary>
    private static string GenerateSeal()
    {
        byte[] entropy = RandomNumberGenerator.GetBytes(SealEntropyBytes);

        // Five bits per symbol, most significant first, with a dash between groups for the eye
        StringBuilder seal = new StringBuilder();
        int bitCount = SealEntropyBytes * 8;
        for (int bitOffset = 0; bitOffset < bitCount; bitOffset += 5)
        {
            if (bitOffset > 0 && bitOffset / 5 % SealGroupLength == 0)
                seal.Append('-');

            int symbol = 0;
            for (int bit = bitOffset; bit < bitOffset + 5; bit++)
                symbol = (symbol << 1) | ((entropy[bit / 8] >> (7 - bit % 8)) & 1);
            seal.Append(CrockfordAlphabet[symbol]);
        }
        return seal.ToString();
    }

    /// <summary>Hashes a seal as typed, after folding away what a person copying it by hand gets wrong.</summary>
    private static byte[] HashSeal(string seal)
    {
        // Case, dashes and spaces carry nothing; O, I and L are the digits Crockford excludes them for
        StringBuilder normalised = new StringBuilder(seal.Length);
        foreach (char character in seal.ToUpperInvariant())
        {
            if (character == '-' || char.IsWhiteSpace(character))
                continue;
            normalised.Append(character switch
            {
                'O' => '0',
                'I' or 'L' => '1',
                _ => character
            });
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes(normalised.ToString()));
    }

    /// <summary>
    /// Points at the conversation's own database. The identifier is sanitized here as it is in the
    /// persistence service: it reaches this layer from a channel and it becomes a file name.
    /// </summary>
    private string GetConnectionString(string conversationId)
    {
        string sanitized = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
        return $"Data Source={Path.Combine(persistenceOptions.StoragePath, $"morgana-{sanitized}.db")}";
    }
}