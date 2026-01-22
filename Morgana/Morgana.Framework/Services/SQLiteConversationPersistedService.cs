using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Interfaces;
using System.Security.Cryptography;
using System.Text.Json;

namespace Morgana.Framework.Services;

/// <summary>
/// SQLite-based conversation persistence service with AES-256 encryption.
/// Stores each conversation in a separate database file: "morgana-{conversationId}.db"
/// Each database contains a single table "morgana" with one row per agent.
/// </summary>
/// <remarks>
/// <para><strong>Storage Model:</strong></para>
/// <code>
/// Database per conversation:
///   {StoragePath}/morgana-conv12345.db
///
/// Table structure (morgana):
///   - agent_identifier: TEXT PRIMARY KEY (e.g., "billing-conv12345")
///   - agent_name: TEXT UNIQUE (e.g., "billing")
///   - conversation_id: TEXT (e.g., "conv12345")
///   - agent_thread: BLOB (AES-256-CBC encrypted AgentThread JSON)
///   - creation_date: TEXT (ISO 8601: "yyyy-MM-ddTHH:mm:ss.fffZ")
///   - last_update: TEXT (ISO 8601: "yyyy-MM-ddTHH:mm:ss.fffZ")
///   - is_active: INTEGER (0 or 1)
/// </code>
/// <para><strong>Concurrency Model:</strong></para>
/// <para>Only ONE agent is active at a time per conversation. No concurrent writes occur.
/// This allows for simplified SQLite configuration without WAL mode or complex locking.</para>
/// <para><strong>Encryption:</strong></para>
/// <para>Agent thread data is encrypted using AES-256-CBC with PKCS7 padding.
/// IV is prepended to ciphertext. Encryption key from appsettings.json.</para>
/// <para><strong>Configuration:</strong></para>
/// <code>
/// // appsettings.json
/// {
///   "Morgana": {
///     "ConversationPersistence": {
///       "StoragePath": "C:/MorganaData",
///       "EncryptionKey": "your-base64-encoded-256-bit-key"
///     }
///   }
/// }
/// </code>
/// </remarks>
public class SQLiteConversationPersistenceService : IConversationPersistenceService
{
    private readonly ILogger<SQLiteConversationPersistenceService> logger;
    private readonly Records.ConversationPersistenceOptions options;
    private readonly byte[] encryptionKey;

    // Database schema version for tracking initialization
    private const int SchemaVersion = 1;

    // SQL statements
    private const string InitializeMorganaTableSQL =
        """
        CREATE TABLE IF NOT EXISTS morgana (
            agent_identifier TEXT PRIMARY KEY NOT NULL,
            agent_name TEXT UNIQUE NOT NULL,
            conversation_id TEXT NOT NULL,
            agent_thread BLOB NOT NULL,
            creation_date TEXT NOT NULL,
            last_update TEXT NOT NULL,
            is_active INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS idx_agent_name ON morgana(agent_name);
        CREATE INDEX IF NOT EXISTS idx_conversation_id ON morgana(conversation_id);
        """;

    private const string SaveAgentConversationSQL =
        """
        INSERT INTO morgana (agent_identifier, agent_name, conversation_id, agent_thread, creation_date, last_update, is_active)
        VALUES (@agent_identifier, @agent_name, @conversation_id, @agent_thread, @creation_date, @last_update, @is_active)
        ON CONFLICT(agent_identifier) DO UPDATE SET
            agent_thread = excluded.agent_thread,
            last_update = @last_update,
            is_active = @is_active;
        """;

    private const string LoadAgentConversationSQL =
        """
        SELECT agent_thread FROM morgana WHERE agent_identifier = @agent_identifier;
        """;

    private const string GetMostRecentActiveAgentSQL =
        """
        SELECT agent_name FROM morgana WHERE is_active = 1 ORDER BY last_update DESC LIMIT 1;
        """;

    /// <summary>
    /// Initializes a new instance of the SQLiteConversationPersistenceService.
    /// Validates configuration and ensures storage directory exists.
    /// </summary>
    /// <param name="options">Configuration options containing storage path and encryption key</param>
    /// <param name="logger">Logger instance for diagnostics</param>
    /// <exception cref="ArgumentException">Thrown if StoragePath or EncryptionKey are not configured</exception>
    public SQLiteConversationPersistenceService(
        IOptions<Records.ConversationPersistenceOptions> options,
        ILogger<SQLiteConversationPersistenceService> logger)
    {
        this.logger = logger;
        this.options = options.Value;

        if (string.IsNullOrWhiteSpace(this.options.StoragePath))
            throw new ArgumentException("StoragePath must be configured in appsettings.json");

        if (string.IsNullOrWhiteSpace(this.options.EncryptionKey))
            throw new ArgumentException("EncryptionKey must be configured in appsettings.json");

        // Ensure storage directory exists
        Directory.CreateDirectory(this.options.StoragePath);

        // Derive encryption key from configured key
        encryptionKey = Convert.FromBase64String(this.options.EncryptionKey);

        if (encryptionKey.Length != 32)
            throw new ArgumentException("EncryptionKey must be a 256-bit (32-byte) key encoded as Base64");

        logger.LogInformation($"{nameof(SQLiteConversationPersistenceService)} initialized with storage path: {this.options.StoragePath}");
    }

    /// <inheritdoc/>
    public async Task SaveAgentConversationAsync(
        string agentIdentifier,
        AgentThread agentThread,
        bool isCompleted,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        try
        {
            // Extract agent_name and conversation_id from agent_identifier (format: "{agent_name}-{conversation_id}")
            string[] parts = agentIdentifier.Split('-', 2);
            if (parts.Length != 2)
                throw new ArgumentException($"Invalid agent_identifier format: '{agentIdentifier}'. Expected format: '{{agent_name}}-{{conversation_id}}'");

            string agentName = parts[0];
            string conversationId = parts[1];

            // Serialize AgentThread to JSON
            JsonElement serialized = agentThread.Serialize(jsonSerializerOptions);
            string json = JsonSerializer.Serialize(serialized, jsonSerializerOptions);

            // Encrypt JSON content
            byte[] encryptedData = Encrypt(json);

            // Get database connection
            string connectionString = GetConnectionString(conversationId);
            await using SqliteConnection connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            // Initialize database only if needed (checked via user_version pragma)
            await EnsureDatabaseInitializedAsync(connection);

            // Upsert agent thread with transaction
            await using SqliteTransaction transaction = connection.BeginTransaction();
            try
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = SaveAgentConversationSQL;

                string utcNow = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                command.Parameters.AddWithValue("@agent_identifier", agentIdentifier);
                command.Parameters.AddWithValue("@agent_name", agentName);
                command.Parameters.AddWithValue("@conversation_id", conversationId);
                command.Parameters.AddWithValue("@agent_thread", encryptedData);
                command.Parameters.AddWithValue("@creation_date", utcNow);
                command.Parameters.AddWithValue("@last_update", utcNow);
                command.Parameters.AddWithValue("@is_active", isCompleted ? 0 : 1);

                await command.ExecuteNonQueryAsync();
                await transaction.CommitAsync();

                logger.LogInformation($"Saved conversation {agentIdentifier} to database ({encryptedData.Length} bytes encrypted)");
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to save conversation {agentIdentifier}");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<AgentThread?> LoadAgentConversationAsync(
        string agentIdentifier,
        MorganaAgent agent,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        try
        {
            // Extract conversation_id from agent_identifier
            string[] parts = agentIdentifier.Split('-', 2);
            if (parts.Length != 2)
                throw new ArgumentException($"Invalid agent_identifier format: '{agentIdentifier}'. Expected format: '{{agent_name}}-{{conversation_id}}'");

            string conversationId = parts[1];

            // Get database connection
            string connectionString = GetConnectionString(conversationId);
            string dbPath = GetDatabasePath(conversationId);

            // Check if database file exists
            if (!File.Exists(dbPath))
            {
                logger.LogInformation($"Conversation database for {agentIdentifier} not found, returning null");
                return null;
            }

            await using SqliteConnection connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            // Query agent thread
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = LoadAgentConversationSQL;
            command.Parameters.AddWithValue("@agent_identifier", agentIdentifier);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync();

            if (!await reader.ReadAsync())
            {
                logger.LogInformation($"Agent thread {agentIdentifier} not found in database, returning null");
                return null;
            }

            // Read encrypted blob
            byte[] encryptedData = (byte[])reader["agent_thread"];

            // Decrypt content
            string json = Decrypt(encryptedData);

            // Deserialize JSON to JsonElement
            JsonElement serialized = JsonSerializer.Deserialize<JsonElement>(json, jsonSerializerOptions);

            // Deserialize thread via MorganaAgent
            AgentThread restored = await agent.DeserializeThreadAsync(serialized, jsonSerializerOptions);

            logger.LogInformation($"Loaded conversation {agentIdentifier} from database ({encryptedData.Length} bytes decrypted)");

            return restored;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to load conversation {agentIdentifier}");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> GetMostRecentActiveAgentAsync(string conversationId)
    {
        try
        {
            string connectionString = GetConnectionString(conversationId);
            string dbPath = GetDatabasePath(conversationId);

            if (!File.Exists(dbPath))
            {
                logger.LogInformation($"Database for conversation {conversationId} not found");
                return null;
            }

            await using SqliteConnection connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();

            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = GetMostRecentActiveAgentSQL;

            object? result = await command.ExecuteScalarAsync();

            string? agentName = result?.ToString();

            logger.LogInformation(
                agentName != null
                    ? $"Most recent agent for conversation {conversationId}: {agentName}"
                    : $"No agents found for conversation {conversationId}");

            return agentName;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to get most recent agent for conversation {conversationId}");
            return null;
        }
    }

    /// <summary>
    /// Constructs the SQLite connection string for a conversation database.
    /// Simple configuration for single-writer scenario (one agent at a time).
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    /// <returns>SQLite connection string</returns>
    private string GetConnectionString(string conversationId)
    {
        string dbPath = GetDatabasePath(conversationId);
        return $"Data Source={dbPath}";
    }

    /// <summary>
    /// Constructs the full file path for a conversation database.
    /// Format: morgana-{conversationId}.db
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    /// <returns>Full path to the database file</returns>
    private string GetDatabasePath(string conversationId)
    {
        // Sanitize conversationId to prevent directory traversal attacks
        string sanitized = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(options.StoragePath, $"morgana-{sanitized}.db");
    }

    /// <summary>
    /// Ensures the database is initialized with schema.
    /// Uses SQLite's user_version pragma to track initialization state - only runs once per database.
    /// </summary>
    /// <param name="connection">Open SQLite connection</param>
    private static async Task EnsureDatabaseInitializedAsync(SqliteConnection connection)
    {
        // Check user_version to see if database is already initialized
        await using SqliteCommand checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "PRAGMA user_version;";
        long currentVersion = (long)(await checkCommand.ExecuteScalarAsync() ?? 0L);

        if (currentVersion >= SchemaVersion)
            return; // Already initialized

        // Create schema
        await using SqliteCommand schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = InitializeMorganaTableSQL;
        await schemaCommand.ExecuteNonQueryAsync();

        // Mark database as initialized
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = $"PRAGMA user_version = {SchemaVersion};";
        await versionCommand.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Encrypts plaintext JSON using AES-256-CBC.
    /// </summary>
    /// <param name="plaintext">JSON string to encrypt</param>
    /// <returns>Encrypted bytes (IV prepended to ciphertext)</returns>
    private byte[] Encrypt(string plaintext)
    {
        using Aes aes = Aes.Create();
        aes.Key = encryptionKey;
        aes.GenerateIV();

        using ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using MemoryStream msEncrypt = new MemoryStream();

        // Write IV to beginning of stream (needed for decryption)
        msEncrypt.Write(aes.IV, 0, aes.IV.Length);

        using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
        using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
        {
            swEncrypt.Write(plaintext);
        }

        return msEncrypt.ToArray();
    }

    /// <summary>
    /// Decrypts encrypted bytes using AES-256-CBC.
    /// </summary>
    /// <param name="ciphertext">Encrypted bytes (IV prepended to ciphertext)</param>
    /// <returns>Decrypted JSON string</returns>
    private string Decrypt(byte[] ciphertext)
    {
        using Aes aes = Aes.Create();
        aes.Key = encryptionKey;

        // Extract IV from beginning of ciphertext
        byte[] iv = new byte[aes.IV.Length];
        Array.Copy(ciphertext, 0, iv, 0, iv.Length);
        aes.IV = iv;

        using ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using MemoryStream msDecrypt = new MemoryStream(ciphertext, iv.Length, ciphertext.Length - iv.Length);
        using CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
        using StreamReader srDecrypt = new StreamReader(csDecrypt);

        return srDecrypt.ReadToEnd();
    }
}