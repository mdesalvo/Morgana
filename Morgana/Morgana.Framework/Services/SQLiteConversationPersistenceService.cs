using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Interfaces;
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
///   - agent_session: BLOB (AES-256-CBC encrypted AgentSession JSON)
///   - creation_date: TEXT (ISO 8601: "yyyy-MM-ddTHH:mm:ss.fffZ")
///   - last_update: TEXT (ISO 8601: "yyyy-MM-ddTHH:mm:ss.fffZ")
///   - is_active: INTEGER (0 or 1)
/// </code>
/// <para><strong>Concurrency Model:</strong></para>
/// <para>Only ONE agent is active at a time per conversation. No concurrent writes occur.
/// This allows for simplified SQLite configuration without WAL mode or complex locking.</para>
/// <para><strong>Encryption:</strong></para>
/// <para>Agent session data is encrypted using AES-256-CBC with PKCS7 padding.
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
    public async Task EnsureDatabaseInitializedAsync(string conversationId)
    {
        string connectionString = GetConnectionString(conversationId);
        await using SqliteConnection connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        await EnsureDatabaseInitializedAsync(connection);
    }

    /// <inheritdoc/>
    public async Task SaveAgentConversationAsync(
        string agentIdentifier,
        AIAgent agent,
        AgentSession agentSession,
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

            // Serialize AgentSession to JSON
            JsonElement agentSessionJsonElement = await agent.SerializeSessionAsync(agentSession, jsonSerializerOptions);
            string agentSessionJsonString = JsonSerializer.Serialize(agentSessionJsonElement, jsonSerializerOptions);

            // Encrypt JSON content
            byte[] encryptedAgentSessionJsonString = Encrypt(agentSessionJsonString);

            // Get database connection
            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // Initialize database only if needed (checked via user_version pragma)
            await EnsureDatabaseInitializedAsync(sqliteConnection);

            // Upsert agent session with transaction
            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
                sqliteCommand.Transaction = sqliteTransaction;
                sqliteCommand.CommandText =
"""
INSERT INTO morgana (agent_identifier, agent_name, agent_session, creation_date, last_update, is_active)
VALUES (@agent_identifier, @agent_name, @agent_session, @creation_date, @last_update, @is_active)
ON CONFLICT(agent_identifier) DO UPDATE SET
    agent_session = excluded.agent_session, last_update = @last_update, is_active = @is_active;
""";

                string utcNow = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                sqliteCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);
                sqliteCommand.Parameters.AddWithValue("@agent_name", agentName);
                sqliteCommand.Parameters.AddWithValue("@agent_session", encryptedAgentSessionJsonString);
                sqliteCommand.Parameters.AddWithValue("@creation_date", utcNow);
                sqliteCommand.Parameters.AddWithValue("@last_update", utcNow);
                sqliteCommand.Parameters.AddWithValue("@is_active", isCompleted ? 0 : 1);

                await sqliteCommand.ExecuteNonQueryAsync();
                await sqliteTransaction.CommitAsync();

                logger.LogInformation($"Saved conversation {agentIdentifier} to database ({encryptedAgentSessionJsonString.Length} bytes encrypted)");
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
    public async Task<AgentSession?> LoadAgentConversationAsync(
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

            // Query agent session
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "SELECT agent_session FROM morgana WHERE agent_identifier = @agent_identifier;";
            sqliteCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);

            await using SqliteDataReader sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();

            if (!await sqliteDataReader.ReadAsync())
            {
                logger.LogInformation($"Agent session {agentIdentifier} not found in SQLite database, returning null");
                return null;
            }

            // Read encrypted blob
            byte[] agentSessionEncryptedJsonString = (byte[])sqliteDataReader["agent_session"];

            // Decrypt content
            string agentSessionJsonString = Decrypt(agentSessionEncryptedJsonString);

            // Deserialize JSON to JsonElement
            JsonElement agentSessionJsonElement = JsonSerializer.Deserialize<JsonElement>(agentSessionJsonString, jsonSerializerOptions);

            // Deserialize session via MorganaAgent
            AgentSession agentSession = await agent.DeserializeSessionAsync(agentSessionJsonElement, jsonSerializerOptions);

            logger.LogInformation($"Loaded conversation {agentIdentifier} from SQLite database ({agentSessionEncryptedJsonString.Length} bytes decrypted)");

            return agentSession;
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
            sqliteCommand.CommandText = "SELECT agent_name FROM morgana WHERE is_active = 1 ORDER BY last_update DESC LIMIT 1;";

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
                return [];
            }

            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "SELECT agent_name, agent_session, is_active FROM morgana ORDER BY creation_date ASC;";

            await using SqliteDataReader sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();

            // Collect all messages from all agents with their completion status
            List<(string agentName, bool agentCompleted, ChatMessage message)> allMessages = [];

            while (await sqliteDataReader.ReadAsync())
            {
                string agentName = (string)sqliteDataReader["agent_name"];
                bool agentCompleted = (long)sqliteDataReader["is_active"] == 0;

                // Decrypt and deserialize agent session
                byte[] encryptedAgentSessionJsonString = (byte[])sqliteDataReader["agent_session"];
                string agentSessionJsonString = Decrypt(encryptedAgentSessionJsonString);
                JsonElement agentSessionJsonElement = JsonSerializer.Deserialize<JsonElement>(
                    agentSessionJsonString, jsonSerializerOptions);

                // Extract messages array from AgentSession structure
                if (!agentSessionJsonElement.TryGetProperty("chatHistoryProviderState", out JsonElement chatHistoryProviderStateElement))
                {
                    logger.LogWarning($"AgentSession for {agentName} missing 'chatHistoryProviderState' property, skipping");
                    continue;
                }
                if (!chatHistoryProviderStateElement.TryGetProperty("messages", out JsonElement messagesElement))
                {
                    logger.LogWarning($"AgentSession.chatHistoryProviderState for {agentName} missing 'messages' property, skipping");
                    continue;
                }

                // Deserialize messages to ChatMessage array
                ChatMessage[]? chatMessages = JsonSerializer.Deserialize<ChatMessage[]>(
                    messagesElement.GetRawText(),
                    jsonSerializerOptions) ?? throw new InvalidOperationException(
                        $"Failed to deserialize Messages for agent {agentName} in conversation {conversationId}");

                // Add all messages with agent metadata
                foreach (ChatMessage message in chatMessages)
                    allMessages.Add((agentName, agentCompleted, message));
            }

            // Delegate filtering and processing to specialized method
            // (ensure to present messages in their effective temporal order,
            //  since they comes from database in agent's appearance order;
            //  also ensure to filter out tool messages)
            return ProcessMessagesForHistory(conversationId,
                allMessages.Where(m => m.message.Role != ChatRole.Tool)
                           .OrderBy(m => m.message.CreatedAt?.UtcDateTime ?? DateTime.UtcNow)
                           .ToList(), jsonSerializerOptions);
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
    /// Internal implementation that works with an already-open connection.
    /// Used by both public API and internal persistence operations.
    /// </summary>
    /// <param name="connection">Open SQLite connection</param>
    private async Task EnsureDatabaseInitializedAsync(SqliteConnection connection)
    {
        // Check user_version to see if database is already initialized
        await using SqliteCommand checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "PRAGMA user_version;";
        long currentVersion = (long)(await checkCommand.ExecuteScalarAsync() ?? 0L);

        if (currentVersion >= 2)
            return; // Already initialized

        // Create schema
        await using SqliteCommand schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText =
"""
CREATE TABLE IF NOT EXISTS morgana (
    agent_identifier TEXT PRIMARY KEY NOT NULL,
    agent_name TEXT UNIQUE NOT NULL,
    agent_session BLOB NOT NULL,
    creation_date TEXT NOT NULL,
    last_update TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS rate_limit_log (
    request_timestamp TEXT NOT NULL
);
""";
        await schemaCommand.ExecuteNonQueryAsync();

        // Mark database as initialized (version 2)
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version = 2;";
        await versionCommand.ExecuteNonQueryAsync();

        logger.LogInformation(
            $"Initialized database schema v2 for: {Path.GetFileName(connection.DataSource)}");
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
    /// Processes raw messages from AgentSession into UI-ready MorganaChatMessage array.
    /// Handles quick reply extraction, message filtering, and chronological ordering.
    /// </summary>
    private MorganaChatMessage[] ProcessMessagesForHistory(
        string conversationId,
        List<(string agentName, bool agentCompleted, ChatMessage message)> allMessages,
        JsonSerializerOptions jsonSerializerOptions)
    {
        // =============================================================================
        // PASS 1A: Extract rich cards from SetRichCard function calls
        // =============================================================================
        Dictionary<string, RichCard> richCardsByCallId = allMessages
            .Where(m => m.message.Role == ChatRole.Assistant)
            .SelectMany(m => m.message.Contents?
                .OfType<FunctionCallContent>()
                .Where(fc => fc.Name == "SetRichCard") ?? [])
            .Select(fc => new
            {
                CallId = fc.CallId,
                RichCard = TryParseRichCardFromDictionary(fc.Arguments, jsonSerializerOptions)
            })
            .Where(x => x.RichCard != null)
            .ToDictionary(x => x.CallId, x => x.RichCard!);

        logger.LogDebug($"Extracted rich cards from {richCardsByCallId.Count} SetRichCard calls");

        // =============================================================================
        // PASS 1B: Extract quick replies from SetQuickReplies function calls
        // =============================================================================
        Dictionary<string, List<QuickReply>> quickRepliesByCallId = allMessages
            .Where(m => m.message.Role == ChatRole.Assistant)
            .SelectMany(m => m.message.Contents?
                .OfType<FunctionCallContent>()
                .Where(fc => fc.Name == "SetQuickReplies") ?? [])
            .Select(fc => new
            {
                CallId = fc.CallId,
                QuickReplies = TryParseQuickRepliesFromDictionary(fc.Arguments, jsonSerializerOptions)
            })
            .Where(x => x.QuickReplies != null)
            .ToDictionary(x => x.CallId, x => x.QuickReplies!);

        logger.LogDebug($"Extracted quick replies from {quickRepliesByCallId.Count} SetQuickReplies calls");

        // =============================================================================
        // PASS 2: Filter and map messages with rich card and quick replies attachments
        // =============================================================================
        List<MorganaChatMessage> historyMessages = [];
        string? pendingQuickRepliesCallId = null;
        string? pendingRichCardCallId = null;
        int msgIndex = 0;
        bool isLastHistoryMessage = false;

        // Before collecting messages from history, ensure to strip out eventual
        // summarization messages which may have been automatically emitted and
        // inserted by the history provider: they are "assistant" messages having
        // text (the summarization sentence) but they don't have an identifier.
        List<(string agentName, bool agentCompleted, ChatMessage message)> filteredMessages =
            [.. allMessages.Where(m => !(m.message.Role == ChatRole.Assistant
                                          && !string.IsNullOrEmpty(m.message.Text)
                                          && string.IsNullOrEmpty(m.message.MessageId)))];

        foreach ((string agentName, bool agentCompleted, ChatMessage chatMessage) in filteredMessages)
        {
            bool chatMessageHasToolCalls = false;

            // Determine if the current message is the last one
            msgIndex++;
            if (msgIndex == filteredMessages.Count)
                isLastHistoryMessage = true;

            // Skip tool messages
            if (chatMessage.Role == ChatRole.Tool)
                continue;

            // Check for SetRichCard function call
            FunctionCallContent? setRichCardCall = chatMessage.Contents?
                .OfType<FunctionCallContent>()
                .FirstOrDefault(fc => fc.Name == "SetRichCard");
            if (setRichCardCall != null)
            {
                pendingRichCardCallId = setRichCardCall.CallId;
                chatMessageHasToolCalls = true;
            }

            // Check for SetQuickReplies function call
            FunctionCallContent? setQuickRepliesCall = chatMessage.Contents?
                .OfType<FunctionCallContent>()
                .FirstOrDefault(fc => fc.Name == "SetQuickReplies");
            if (setQuickRepliesCall != null)
            {
                pendingQuickRepliesCallId = setQuickRepliesCall.CallId;
                chatMessageHasToolCalls = true;
            }

            // Skip message if it contains any tool calls
            if (chatMessageHasToolCalls)
                continue;

            // Process messages with text content
            string messageText = ExtractTextFromMessage(chatMessage);
            if (string.IsNullOrWhiteSpace(messageText))
                continue;

            // Attach rich card to assistant message following SetRichCard
            RichCard? richCard = null;
            if (pendingRichCardCallId != null
                 && chatMessage.Role == ChatRole.Assistant
                 && richCardsByCallId.TryGetValue(pendingRichCardCallId, out richCard))
            {
                pendingRichCardCallId = null; // Reset after attachment
            }

            // Attach quick replies to assistant message following SetQuickReplies
            List<QuickReply>? quickReplies = null;
            if (pendingQuickRepliesCallId != null
                 && chatMessage.Role == ChatRole.Assistant
                 && quickRepliesByCallId.TryGetValue(pendingQuickRepliesCallId, out quickReplies))
            {
                pendingQuickRepliesCallId = null; // Reset after attachment
            }

            // Add message with both attachments (if present)
            historyMessages.Add(
                MapToMorganaChatMessage(conversationId, agentName, agentCompleted,
                                        chatMessage, isLastHistoryMessage, quickReplies, richCard));
        }

        logger.LogInformation(
            $"Processed {historyMessages.Count} messages (filtered from {allMessages.Count} raw messages) for {conversationId}");

        return historyMessages.ToArray();
    }

    /// <summary>
    /// Attempts to parse quick replies from SetQuickReplies function call arguments dictionary.
    /// Returns null if parsing fails (graceful degradation).
    /// </summary>
    private List<QuickReply>? TryParseQuickRepliesFromDictionary(
        IDictionary<string, object?>? arguments,
        JsonSerializerOptions jsonSerializerOptions)
    {
        if (arguments == null || !arguments.TryGetValue("quickReplies", out object? quickRepliesValue))
            return null;

        try
        {
            // The value might be a string (JSON) or already a JsonElement
            string quickRepliesString = quickRepliesValue switch
            {
                string str => str,
                JsonElement jsonElement => jsonElement.GetString() ?? "[]",
                _ => JsonSerializer.Serialize(quickRepliesValue, jsonSerializerOptions)
            };

            List<QuickReply>? quickReplies = JsonSerializer.Deserialize<List<QuickReply>>(
                quickRepliesString,
                jsonSerializerOptions);

            return quickReplies?.Count > 0 ? quickReplies : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse quick replies from function arguments");
            return null;
        }
    }

    /// <summary>
    /// Attempts to parse rich cards from SetRichCard function call arguments dictionary.
    /// Returns null if parsing fails (graceful degradation).
    /// </summary>
    private RichCard? TryParseRichCardFromDictionary(
        IDictionary<string, object?>? arguments,
        JsonSerializerOptions jsonSerializerOptions)
    {
        if (arguments == null || !arguments.TryGetValue("richCard", out object? richCardsValue))
            return null;

        try
        {
            // The value might be a string (JSON) or already a JsonElement
            string richCardString = richCardsValue switch
            {
                string str => str,
                JsonElement jsonElement => jsonElement.GetString() ?? "[]",
                _ => JsonSerializer.Serialize(richCardsValue, jsonSerializerOptions)
            };

            return JsonSerializer.Deserialize<RichCard>(
                richCardString,
                jsonSerializerOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse rich card from function arguments");
            return null;
        }
    }

    /// <summary>
    /// Extracts and concatenates all TextContent blocks from a ChatMessage.
    /// </summary>
    private string ExtractTextFromMessage(ChatMessage chatMessage)
    {
        if (chatMessage.Contents == null || chatMessage.Contents.Count == 0)
            return string.Empty;

        return string.Join(" ", chatMessage.Contents
            .OfType<TextContent>()
            .Where(tc => !string.IsNullOrEmpty(tc.Text))
            .Select(tc => tc.Text.Trim()));

        //TODO: in future we may have more content types handled here
    }

    /// <summary>
    /// Maps a Microsoft.Agents.AI.ChatMessage to MorganaChatMessage record
    /// </summary>
    private MorganaChatMessage MapToMorganaChatMessage(
        string conversationId,
        string agentName,
        bool agentCompleted,
        ChatMessage chatMessage,
        bool isLastHistoryMessage,
        List<QuickReply>? quickReplies = null,
        RichCard? richCard = null)
    {
        string messageText = ExtractTextFromMessage(chatMessage);

        // Determine message type from role
        MessageType messageType = chatMessage.Role == ChatRole.User
            ? MessageType.User
            : MessageType.Assistant;

        // Format agent name for UI
        string displayAgentName = chatMessage.Role == ChatRole.User
            ? "User"
            : string.IsNullOrEmpty(agentName) || agentName.Equals("morgana", StringComparison.OrdinalIgnoreCase)
                ? "Morgana"
                : $"Morgana ({char.ToUpper(agentName[0])}{agentName[1..]})";

        return new MorganaChatMessage
        {
            ConversationId = conversationId,
            Text = messageText,
            Timestamp = chatMessage.CreatedAt?.UtcDateTime ?? DateTime.UtcNow,
            Type = messageType,
            AgentName = displayAgentName,
            AgentCompleted = agentCompleted,
            QuickReplies = quickReplies,
            IsLastHistoryMessage = isLastHistoryMessage,
            RichCard = richCard
        };
    }

    #endregion
}