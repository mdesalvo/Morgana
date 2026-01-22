using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Interfaces;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using static Morgana.Framework.Records;

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
    private readonly ILogger logger;
    private readonly ConversationPersistenceOptions options;
    private readonly byte[] encryptionKey;

    /// <summary>
    /// Initializes a new instance of the SQLiteConversationPersistenceService.
    /// Validates configuration and ensures storage directory exists.
    /// </summary>
    /// <param name="options">Configuration options containing storage path and encryption key</param>
    /// <param name="logger">Logger instance for diagnostics</param>
    /// <exception cref="ArgumentException">Thrown if StoragePath or EncryptionKey are not configured</exception>
    public SQLiteConversationPersistenceService(
        IOptions<ConversationPersistenceOptions> options,
        ILogger logger)
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
            string[] agentIdentifierParts = agentIdentifier.Split('-', 2);
            if (agentIdentifierParts.Length != 2)
                throw new ArgumentException($"Invalid agent_identifier format: '{agentIdentifier}'. Expected format: '{{agent_name}}-{{conversation_id}}'");

            string agentName = agentIdentifierParts[0];
            string conversationId = agentIdentifierParts[1];

            // Serialize AgentThread to JSON
            JsonElement agentThreadJsonElement = agentThread.Serialize(jsonSerializerOptions);
            string agentThreadJsonString = JsonSerializer.Serialize(agentThreadJsonElement, jsonSerializerOptions);

            // Encrypt JSON content
            byte[] encryptedAgentThreadJsonString = Encrypt(agentThreadJsonString);

            // Get database connection
            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // Initialize database only if needed (checked via user_version pragma)
            await EnsureDatabaseInitializedAsync(sqliteConnection);

            // Upsert agent thread with transaction
            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
                sqliteCommand.Transaction = sqliteTransaction;
                sqliteCommand.CommandText =
"""
INSERT INTO morgana (agent_identifier, agent_name, conversation_id, agent_thread, creation_date, last_update, is_active)
VALUES (@agent_identifier, @agent_name, @conversation_id, @agent_thread, @creation_date, @last_update, @is_active)
ON CONFLICT(agent_identifier) DO UPDATE SET
    agent_thread = excluded.agent_thread, last_update = @last_update, is_active = @is_active;
""";

                string utcNow = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                sqliteCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);
                sqliteCommand.Parameters.AddWithValue("@agent_name", agentName);
                sqliteCommand.Parameters.AddWithValue("@conversation_id", conversationId);
                sqliteCommand.Parameters.AddWithValue("@agent_thread", encryptedAgentThreadJsonString);
                sqliteCommand.Parameters.AddWithValue("@creation_date", utcNow);
                sqliteCommand.Parameters.AddWithValue("@last_update", utcNow);
                sqliteCommand.Parameters.AddWithValue("@is_active", isCompleted ? 0 : 1);

                await sqliteCommand.ExecuteNonQueryAsync();
                await sqliteTransaction.CommitAsync();

                logger.LogInformation($"Saved conversation {agentIdentifier} to database ({encryptedAgentThreadJsonString.Length} bytes encrypted)");
            }
            catch
            {
                await sqliteTransaction.RollbackAsync();
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
            string[] agentIdentifierParts = agentIdentifier.Split('-', 2);
            if (agentIdentifierParts.Length != 2)
                throw new ArgumentException($"Invalid agent_identifier format: '{agentIdentifier}'. Expected format: '{{agent_name}}-{{conversation_id}}'");

            string conversationId = agentIdentifierParts[1];

            // Get database connection
            string sqliteConnectionString = GetConnectionString(conversationId);
            string sqliteDbPath = GetDatabasePath(conversationId);

            // Check if database file exists
            if (!File.Exists(sqliteDbPath))
            {
                logger.LogInformation($"Conversation SQLite database for {agentIdentifier} not found, returning null");
                return null;
            }

            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // Query agent thread
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText =
"""
SELECT agent_thread FROM morgana WHERE agent_identifier = @agent_identifier;
""";
            sqliteCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);

            await using SqliteDataReader sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();

            if (!await sqliteDataReader.ReadAsync())
            {
                logger.LogInformation($"Agent thread {agentIdentifier} not found in SQLite database, returning null");
                return null;
            }

            // Read encrypted blob
            byte[] agentThreadEncryptedJsonString = (byte[])sqliteDataReader["agent_thread"];

            // Decrypt content
            string agentThreadJsonString = Decrypt(agentThreadEncryptedJsonString);

            // Deserialize JSON to JsonElement
            JsonElement agentThreadJsonElement = JsonSerializer.Deserialize<JsonElement>(agentThreadJsonString, jsonSerializerOptions);

            // Deserialize thread via MorganaAgent
            AgentThread agentThread = await agent.DeserializeThreadAsync(agentThreadJsonElement, jsonSerializerOptions);

            logger.LogInformation($"Loaded conversation {agentIdentifier} from SQLite database ({agentThreadEncryptedJsonString.Length} bytes decrypted)");

            return agentThread;
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
            string sqliteConnectionString = GetConnectionString(conversationId);
            string sqliteDbPath = GetDatabasePath(conversationId);

            if (!File.Exists(sqliteDbPath))
            {
                logger.LogInformation($"SQLite database for conversation {conversationId} not found");
                return null;
            }

            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText =
"""
SELECT agent_name FROM morgana WHERE is_active = 1 ORDER BY last_update DESC LIMIT 1;
""";

            object? result = await sqliteCommand.ExecuteScalarAsync();

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

    /// <inheritdoc/>
    public async Task<MorganaChatMessage[]> GetConversationHistoryAsync(
        string conversationId,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        try
        {
            string sqliteConnectionString = GetConnectionString(conversationId);
            string sqliteDbPath = GetDatabasePath(conversationId);

            // Early return if database doesn't exist
            if (!File.Exists(sqliteDbPath))
            {
                logger.LogInformation($"SQLite database for conversation {conversationId} not found, returning empty history");
                return Array.Empty<MorganaChatMessage>();
            }

            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText =
"""
SELECT agent_name, agent_thread, is_active FROM morgana ORDER BY creation_date ASC;
""";

            await using SqliteDataReader sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();

            // Collect all messages from all agents with their completion status
            List<(string agentName, bool agentCompleted, ChatMessage message)> allMessages = [];

            while (await sqliteDataReader.ReadAsync())
            {
                string agentName = (string)sqliteDataReader["agent_name"];
                bool agentCompleted = (long)sqliteDataReader["is_active"] == 0;

                // Decrypt and deserialize agent thread
                byte[] encryptedAgentThreadJsonString = (byte[])sqliteDataReader["agent_thread"];
                string agentThreadJsonString = Decrypt(encryptedAgentThreadJsonString);
                JsonElement agentThreadJsonElement = JsonSerializer.Deserialize<JsonElement>(
                    agentThreadJsonString,
                    jsonSerializerOptions);

                // Extract messages array from AgentThread structure
                if (!agentThreadJsonElement.TryGetProperty("storeState", out JsonElement storeStateElement))
                {
                    logger.LogWarning($"AgentThread for {agentName} missing 'storeState' property, skipping");
                    continue;
                }
                if (!storeStateElement.TryGetProperty("messages", out JsonElement messagesElement))
                {
                    logger.LogWarning($"AgentThread.storeState for {agentName} missing 'messages' property, skipping");
                    continue;
                }

                // Deserialize messages to ChatMessage array
                ChatMessage[]? messages = JsonSerializer.Deserialize<ChatMessage[]>(
                    messagesElement.GetRawText(),
                    jsonSerializerOptions) ?? throw new InvalidOperationException(
                        $"Failed to deserialize Messages for agent {agentName} in conversation {conversationId}");

                // Add all messages with agent metadata
                foreach (ChatMessage message in messages)
                    allMessages.Add((agentName, agentCompleted, message));
            }

            // Sort messages chronologically by CreatedAt
            // and filter out agent's technical messages
            MorganaChatMessage[] chatMessages = allMessages
                .Where(m => !string.IsNullOrEmpty(m.message.Text))
                .OrderBy(m => m.message.CreatedAt)
                .Select(m => MapToMorganaChatMessage(conversationId, m.agentName, m.agentCompleted, m.message))
                .ToArray();

            logger.LogInformation(
                $"Retrieved conversation history for {conversationId}: {chatMessages.Length} messages from {allMessages.Select(m => m.agentName).Distinct().Count()} agents");

            return chatMessages;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to retrieve conversation history for {conversationId}");
            throw;
        }
    }

    #region Utilities

    /// <summary>
    /// Constructs the SQLite connection string for a conversation database.
    /// Simple configuration for single-writer scenario (one agent at a time).
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    /// <returns>SQLite connection string</returns>
    private string GetConnectionString(string conversationId)
    {
        string sqliteDbPath = GetDatabasePath(conversationId);
        return $"Data Source={sqliteDbPath}";
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
        string sanitizedConversationId = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(options.StoragePath, $"morgana-{sanitizedConversationId}.db");
    }

    /// <summary>
    /// Ensures the database is initialized with schema.
    /// Uses SQLite's user_version pragma to track initialization state - only runs once per database.
    /// </summary>
    /// <param name="connection">Open SQLite connection</param>
    private async Task EnsureDatabaseInitializedAsync(SqliteConnection connection)
    {
        // Check user_version to see if database is already initialized
        await using SqliteCommand checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "PRAGMA user_version;";
        long currentVersion = (long)(await checkCommand.ExecuteScalarAsync() ?? 0L);

        if (currentVersion >= 1)
            return; // Already initialized

        // Create schema
        await using SqliteCommand schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText =
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

CREATE INDEX IF NOT EXISTS idx_conversation_id ON morgana(conversation_id);
""";
        await schemaCommand.ExecuteNonQueryAsync();

        // Mark database as initialized (once, forever)
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version = 1;";
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

    /// <summary>
    /// Maps a Microsoft.Agents.AI.ChatMessage to MorganaChatMessage record.
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    /// <param name="agentName">Name of the agent that generated the message (e.g., "billing", "contract")</param>
    /// <param name="agentCompleted">Whether the agent has completed its task (from SQLite is_active column)</param>
    /// <param name="chatMessage">Source ChatMessage from AgentThread</param>
    /// <returns>Mapped MorganaChatMessage for UI consumption</returns>
    /// <remarks>
    /// <para><strong>Text Extraction:</strong></para>
    /// <para>Concatenates all TextContent blocks in ChatMessage.Content with space separator.
    /// Non-text content (images, files, etc.) is ignored as Cauldron has no multimedia capabilities yet.</para>
    /// <para><strong>Agent Name Formatting:</strong></para>
    /// <list type="bullet">
    /// <item>User messages: "User"</item>
    /// <item>Assistant messages: "Morgana ({agentName})" e.g., "Morgana (Billing)"</item>
    /// </list>
    /// </remarks>
    private MorganaChatMessage MapToMorganaChatMessage(
        string conversationId,
        string agentName,
        bool agentCompleted,
        ChatMessage chatMessage)
    {
        // Extract and concatenate all TextContent blocks
        string messageText = string.Empty;
        if (chatMessage.Contents?.Count > 0)
        {
            IEnumerable<string> textContents = chatMessage.Contents
                .OfType<TextContent>() //Exclude other types of content (Cauldron has not multimedia capabilities yet...)
                .Where(tc => !string.IsNullOrEmpty(tc.Text))
                .Select(tc => tc.Text!);

            messageText = string.Join(" ", textContents);
        }

        // Determine message type from role
        MessageType messageType = MessageType.Assistant;
        string displayAgentName = $"Morgana ({char.ToUpperInvariant(agentName[0]) + agentName[1..]})";
        if (chatMessage.Role == ChatRole.User)
        {
            messageType = MessageType.User;
            displayAgentName = "User";
        }

        return new MorganaChatMessage
        {
            ConversationId = conversationId,
            Text = messageText,
            Timestamp = chatMessage.CreatedAt.GetValueOrDefault().UtcDateTime,
            Type = messageType,
            AgentName = displayAgentName,
            AgentCompleted = agentCompleted
        };
    }

    #endregion
}