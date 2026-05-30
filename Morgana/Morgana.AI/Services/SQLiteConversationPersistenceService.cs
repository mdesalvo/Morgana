using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Morgana.Contracts;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

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

        logger.LogInformation("{SqLiteConversationPersistenceServiceName} initialized with storage path: {OptionsStoragePath}", nameof(SQLiteConversationPersistenceService), this.options.StoragePath);
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

                logger.LogInformation("Saved conversation {AgentIdentifier} to database ({Length} bytes encrypted)", agentIdentifier, encryptedAgentSessionJsonString.Length);
            }
            catch
            {
                await sqliteTransaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save conversation {AgentIdentifier}", agentIdentifier);
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
                logger.LogInformation("Conversation SQLite database for {AgentIdentifier} not found, returning null", agentIdentifier);
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
                logger.LogInformation("Agent session {AgentIdentifier} not found in SQLite database, returning null", agentIdentifier);
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

            logger.LogInformation("Loaded conversation {AgentIdentifier} from SQLite database ({Length} bytes decrypted)", agentIdentifier, agentSessionEncryptedJsonString.Length);

            return agentSession;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load conversation {AgentIdentifier}", agentIdentifier);
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
                logger.LogInformation("SQLite database for conversation {ConversationId} not found", conversationId);
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
            logger.LogError(ex, "Failed to get most recent agent for conversation {ConversationId}", conversationId);
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
                logger.LogInformation("SQLite database for conversation {ConversationId} not found, returning empty history", conversationId);
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

                // Extract messages array from AgentSession structure.
                // Microsoft.Agents.AI framework serializes provider state under: stateBag → {StateKey} → {payload}.
                // MorganaChatHistoryProvider.StateKey = "MorganaChatHistoryProvider"
                if (!agentSessionJsonElement.TryGetProperty("stateBag", out JsonElement stateBagElement))
                {
                    logger.LogWarning("AgentSession for {AgentName} missing 'stateBag' property, skipping", agentName);
                    continue;
                }
                if (!stateBagElement.TryGetProperty("MorganaChatHistoryProvider", out JsonElement chatHistoryProviderStateElement))
                {
                    logger.LogWarning("AgentSession.stateBag for {AgentName} missing 'MorganaChatHistoryProvider' property, skipping", agentName);
                    continue;
                }
                if (!chatHistoryProviderStateElement.TryGetProperty("messages", out JsonElement messagesElement))
                {
                    logger.LogWarning("AgentSession.stateBag.MorganaChatHistoryProvider for {AgentName} missing 'messages' property, skipping", agentName);
                    continue;
                }

                // Deserialize messages to ChatMessage array
                ChatMessage[]? chatMessages = JsonSerializer.Deserialize<ChatMessage[]>(
                    messagesElement.GetRawText(),
                    jsonSerializerOptions) ?? throw new InvalidOperationException(
                        $"Failed to deserialize Messages for agent {agentName} in conversation {conversationId}");

                // Add all messages with agent metadata.
                // Filtering of intermediate (non-user-facing) assistant messages happens later in
                // ProcessMessagesForHistory, AFTER the SetRichCard / SetQuickReplies extraction
                // passes, so widgets attached to intermediate messages survive the filter and get
                // bound to the surviving final assistant message of the turn.
                allMessages.AddRange(
                    chatMessages.Select(message => (agentName, agentCompleted, message)));
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
            logger.LogError(ex, "Failed to retrieve conversation history for {ConversationId}", conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task SaveChannelMetadataAsync(string conversationId, ChannelMetadata metadata)
    {
        try
        {
            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // First-writer pattern: channel handshake may precede any agent execution,
            // so we must guarantee the schema exists before the upsert.
            await EnsureDatabaseInitializedAsync(sqliteConnection);

            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText =
"""
INSERT INTO channel_metadata
    (id, channel_name, delivery_mode, callback_url, supports_rich_cards, supports_quick_replies, supports_streaming, supports_markdown, max_message_length)
VALUES
    (1, @channel_name, @delivery_mode, @callback_url, @supports_rich_cards, @supports_quick_replies, @supports_streaming, @supports_markdown, @max_message_length)
ON CONFLICT(id) DO UPDATE SET
    channel_name           = excluded.channel_name,
    delivery_mode          = excluded.delivery_mode,
    callback_url           = excluded.callback_url,
    supports_rich_cards    = excluded.supports_rich_cards,
    supports_quick_replies = excluded.supports_quick_replies,
    supports_streaming     = excluded.supports_streaming,
    supports_markdown      = excluded.supports_markdown,
    max_message_length     = excluded.max_message_length;
""";
            sqliteCommand.Parameters.AddWithValue("@channel_name", metadata.Coordinates.ChannelName);
            sqliteCommand.Parameters.AddWithValue("@delivery_mode", metadata.Coordinates.DeliveryMode);
            sqliteCommand.Parameters.AddWithValue("@callback_url",
                string.IsNullOrWhiteSpace(metadata.Coordinates.CallbackUrl) ? DBNull.Value : metadata.Coordinates.CallbackUrl);
            sqliteCommand.Parameters.AddWithValue("@supports_rich_cards", metadata.Capabilities.SupportsRichCards ? 1 : 0);
            sqliteCommand.Parameters.AddWithValue("@supports_quick_replies", metadata.Capabilities.SupportsQuickReplies ? 1 : 0);
            sqliteCommand.Parameters.AddWithValue("@supports_streaming", metadata.Capabilities.SupportsStreaming ? 1 : 0);
            sqliteCommand.Parameters.AddWithValue("@supports_markdown", metadata.Capabilities.SupportsMarkdown ? 1 : 0);
            sqliteCommand.Parameters.AddWithValue("@max_message_length",
                metadata.Capabilities.MaxMessageLength.HasValue ? metadata.Capabilities.MaxMessageLength.Value : DBNull.Value);

            await sqliteCommand.ExecuteNonQueryAsync();

            logger.LogInformation(
                "Saved channel metadata for conversation {ConversationId}: channel={Channel}, delivery={Delivery}, callback={Callback}, rc={Rc}, qr={Qr}, str={Str}, md={Md}, max={Max}",
                conversationId,
                metadata.Coordinates.ChannelName,
                metadata.Coordinates.DeliveryMode,
                metadata.Coordinates.CallbackUrl ?? "(none)",
                metadata.Capabilities.SupportsRichCards,
                metadata.Capabilities.SupportsQuickReplies,
                metadata.Capabilities.SupportsStreaming,
                metadata.Capabilities.SupportsMarkdown,
                metadata.Capabilities.MaxMessageLength);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save channel metadata for conversation {ConversationId}", conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<ChannelMetadata?> LoadChannelMetadataAsync(string conversationId)
    {
        try
        {
            string sqliteDbPath = GetDatabasePath(conversationId);
            if (!File.Exists(sqliteDbPath))
            {
                logger.LogInformation("SQLite database for conversation {ConversationId} not found, no persisted channel metadata", conversationId);
                return null;
            }

            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText =
"""
SELECT channel_name, delivery_mode, callback_url, supports_rich_cards, supports_quick_replies, supports_streaming, supports_markdown, max_message_length
FROM channel_metadata
WHERE id = 1;
""";

            await using SqliteDataReader reader = await sqliteCommand.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
            {
                logger.LogInformation("No channel_metadata row found for conversation {ConversationId}", conversationId);
                return null;
            }

            ChannelCapabilities capabilities = new ChannelCapabilities(
                SupportsRichCards: (long)reader["supports_rich_cards"] == 1,
                SupportsQuickReplies: (long)reader["supports_quick_replies"] == 1,
                SupportsStreaming: (long)reader["supports_streaming"] == 1,
                SupportsMarkdown: (long)reader["supports_markdown"] == 1,
                MaxMessageLength: reader["max_message_length"] is DBNull ? null : (int?)(long)reader["max_message_length"]);

            ChannelMetadata metadata = new ChannelMetadata
            {
                Coordinates = new ChannelCoordinates
                {
                    ChannelName = (string)reader["channel_name"],
                    DeliveryMode = (string)reader["delivery_mode"],
                    CallbackUrl = reader["callback_url"] is DBNull ? null : (string)reader["callback_url"]
                },
                Capabilities = capabilities
            };

            logger.LogInformation("Loaded persisted channel metadata for conversation {ConversationId}", conversationId);
            return metadata;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // SQLITE_ERROR (e.g. table missing on legacy DB)
        {
            logger.LogInformation("channel_metadata table missing for conversation {ConversationId} (legacy DB), returning null", conversationId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load channel metadata for conversation {ConversationId}", conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task UpsertSharedVariableAsync(string conversationId, string variableName, object variableValue, string sourceAgentIntent)
    {
        try
        {
            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // First-writer pattern: a shared variable can be written before the agent has saved
            // its first session, so the schema must be guaranteed before the upsert.
            await EnsureDatabaseInitializedAsync(sqliteConnection);

            // Serialize the value as JSON, then encrypt for at-rest parity with agent_session.
            // Shared variables are typically short strings (e.g. userId), so the overhead is
            // negligible and the encryption keeps the on-disk shape coherent across the schema.
            string serialized = JsonSerializer.Serialize(variableValue);
            byte[] encrypted = Encrypt(serialized);

            // INSERT OR IGNORE enforces first-write-wins at the storage layer. The first agent
            // to claim a variable name owns it for the lifetime of the conversation; subsequent
            // upserts (from this agent or any other) silently no-op. This mirrors the first-wins
            // rule that MorganaAIContextProvider.MergeSharedContext applies on the read side at
            // hydration time.
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText =
                """
                INSERT OR IGNORE INTO shared_context
                    (variable_name, variable_value, source_agent_intent, last_update)
                VALUES (@variable_name, @variable_value, @source_agent_intent, @last_update);
                """;
            sqliteCommand.Parameters.AddWithValue("@variable_name", variableName);
            sqliteCommand.Parameters.AddWithValue("@variable_value", encrypted);
            sqliteCommand.Parameters.AddWithValue("@source_agent_intent", sourceAgentIntent);
            sqliteCommand.Parameters.AddWithValue("@last_update", DateTime.UtcNow.ToString("O"));

            int rowsAffected = await sqliteCommand.ExecuteNonQueryAsync();

            logger.LogInformation(
                rowsAffected > 0
                    ? "Shared variable '{VariableName}' persisted by '{Source}' for conversation {ConversationId} (first writer)"
                    : "Shared variable '{VariableName}' from '{Source}' for conversation {ConversationId} ignored (already claimed by an earlier writer)",
                variableName, sourceAgentIntent, conversationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to upsert shared variable '{VariableName}' for conversation {ConversationId}", variableName, conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, object>> LoadSharedVariablesAsync(string conversationId)
    {
        try
        {
            string sqliteConnectionString = GetConnectionString(conversationId);
            string sqliteDbPath = GetDatabasePath(conversationId);

            // No DB → no shared variables. The conversation may simply have not started yet,
            // or the channel handshake may be the only thing that has run so far.
            if (!File.Exists(sqliteDbPath))
                return [];

            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "SELECT variable_name, variable_value FROM shared_context;";

            Dictionary<string, object> sharedVariables = new();
            await using SqliteDataReader sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
            while (await sqliteDataReader.ReadAsync())
            {
                string variableName = (string)sqliteDataReader["variable_name"];
                byte[] encrypted = (byte[])sqliteDataReader["variable_value"];

                string decrypted = Decrypt(encrypted);
                JsonElement element = JsonSerializer.Deserialize<JsonElement>(decrypted);

                // Convert back to a "natural" .NET value so callers (and the LLM via
                // GetContextVariable) see the same shape that was originally written. Without
                // this unwrap, primitives would round-trip as JsonElement and the framework's
                // tool-result serialiser would re-encode them with extra JSON wrapping.
                object? value = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.TryGetInt64(out long l) ? (object)l : element.GetDouble(),
                    JsonValueKind.True   => true,
                    JsonValueKind.False  => false,
                    JsonValueKind.Null   => null,
                    _                    => element, // arrays/objects: keep as JsonElement
                };
                if (value is not null)
                    sharedVariables[variableName] = value;
            }

            return sharedVariables;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) // SQLITE_ERROR (table missing on legacy DB)
        {
            logger.LogInformation("shared_context table missing for conversation {ConversationId} (legacy DB), returning empty registry", conversationId);
            return [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load shared variables for conversation {ConversationId}", conversationId);
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

    /// <inheritdoc />
    public bool ConversationExists(string conversationId)
    {
        return File.Exists(GetDatabasePath(conversationId));
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

        if (currentVersion >= 5)
            return; // Already initialized

        // Create schema. CREATE TABLE IF NOT EXISTS makes this safe to run on databases that
        // are already at an earlier version — existing tables are left intact and only the
        // tables introduced by later versions (shared_context in v4; dust_budget +
        // dust_usage_log in v5) are created.
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

CREATE TABLE IF NOT EXISTS channel_metadata (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    channel_name           TEXT NOT NULL,
    delivery_mode          TEXT NOT NULL,
    callback_url           TEXT NULL,
    supports_rich_cards    INTEGER NOT NULL,
    supports_quick_replies INTEGER NOT NULL,
    supports_streaming     INTEGER NOT NULL,
    supports_markdown      INTEGER NOT NULL,
    max_message_length     INTEGER NULL
);

CREATE TABLE IF NOT EXISTS shared_context (
    variable_name        TEXT PRIMARY KEY NOT NULL,
    variable_value       BLOB NOT NULL,
    source_agent_intent  TEXT NOT NULL,
    last_update          TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS dust_budget (
    id                 INTEGER PRIMARY KEY CHECK (id = 1),
    dust_consumed      REAL    NOT NULL DEFAULT 0,
    warning_70_sent    INTEGER NOT NULL DEFAULT 0,
    warning_90_sent    INTEGER NOT NULL DEFAULT 0
);
INSERT OR IGNORE INTO dust_budget (id) VALUES (1);

CREATE TABLE IF NOT EXISTS dust_usage_log (
    timestamp     TEXT NOT NULL,
    dust_consumed REAL NOT NULL,
    llm_role      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_dust_usage_log_ts ON dust_usage_log(timestamp);
""";
        await schemaCommand.ExecuteNonQueryAsync();

        // Mark database as initialized (version 5)
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version = 5;";
        await versionCommand.ExecuteNonQueryAsync();

        logger.LogInformation(
            "Initialized database schema v5 for: {GetFileName}", Path.GetFileName(connection.DataSource));
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

        logger.LogDebug("Extracted rich cards from {Count} SetRichCard calls", richCardsByCallId.Count);

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

        logger.LogDebug("Extracted quick replies from {Count} SetQuickReplies calls", quickRepliesByCallId.Count);

        // =============================================================================
        // PASS 2: Filter and map messages with rich card and quick replies attachments
        // =============================================================================
        List<MorganaChatMessage> historyMessages = [];
        string? pendingQuickRepliesCallId = null;
        string? pendingRichCardCallId = null;
        int msgIndex = 0;
        bool isLastHistoryMessage = false;

        // Set of agents whose persisted history carries at least one user_facing marker. Within
        // those agents we filter out intermediate (non-user-facing) assistant messages so the
        // tool-use scratchpad doesn't appear in the rendered transcript on resume. Agents whose
        // sessions have no marker (e.g. persisted before this feature shipped) follow the legacy
        // path: every assistant message is rendered, just like before.
        HashSet<string> markedAgents = allMessages
            .Where(m => m.message.Role == ChatRole.Assistant
                     && m.message.AdditionalProperties?.ContainsKey(MorganaChatHistoryProvider.UserFacingMarkerKey) == true)
            .Select(m => m.agentName)
            .ToHashSet();

        // NOTE: the MEAI SummarizingChatReducer annotates the anchor message with
        // `AdditionalProperties["__summary__"]` when it reduces the view for the LLM.
        // That anchor is a real, user-visible turn — it must NOT be filtered out here,
        // otherwise quick replies/rich cards attached to it leak into the next turn.

        foreach ((string agentName, bool agentCompleted, ChatMessage chatMessage) in allMessages)
        {
            bool chatMessageHasToolCalls = false;

            // Determine if the current message is the last one
            msgIndex++;
            if (msgIndex == allMessages.Count)
                isLastHistoryMessage = true;

            // Skip tool messages
            if (chatMessage.Role == ChatRole.Tool)
                continue;

            // A user turn closes any pending assistant attachment that was never
            // consumed (e.g. because the anchor reply arrived with empty text).
            // Without this, a stale pending CallId would bleed into the next
            // assistant response and attach the previous turn's widgets to it.
            if (chatMessage.Role == ChatRole.User)
            {
                pendingRichCardCallId = null;
                pendingQuickRepliesCallId = null;
            }

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

            // Decide whether this assistant message is the user-facing one for its turn. The
            // marker is set by MorganaAgent at end-of-turn on the last assistant message that
            // actually carries text — see MorganaAgent.ExecuteAgentAsync.
            bool isUserFacing = chatMessage.Role == ChatRole.Assistant
                             && chatMessage.AdditionalProperties?.ContainsKey(MorganaChatHistoryProvider.UserFacingMarkerKey) == true;

            // Tool-call messages are normally skipped because their widgets get attached to a
            // separate, text-bearing assistant message later in the turn. The user-facing
            // assistant is the one exception: for layouts where the same message carries BOTH
            // text and a SetRichCard / SetQuickReplies call (typical of Haiku-class models that
            // close the turn without emitting a follow-up empty assistant), this message owns
            // the visible text AND the widgets. Falling through to text rendering keeps them
            // bound together in a single bubble.
            if (chatMessageHasToolCalls && !isUserFacing)
                continue;

            // Skip intermediate assistant messages (no marker) for agents whose session contains
            // at least one user_facing marker. Widgets that those messages may have introduced
            // are already in pendingRichCardCallId / pendingQuickRepliesCallId from a few lines
            // above, ready to be attached to the surviving final assistant.
            if (chatMessage.Role == ChatRole.Assistant && markedAgents.Contains(agentName) && !isUserFacing)
                continue;

            // Process messages with text content
            string messageText = ExtractTextFromMessage(chatMessage);
            if (string.IsNullOrWhiteSpace(messageText))
                continue;

            // Attach rich card to assistant message following SetRichCard
            RichCard? richCard = null;
            if (pendingRichCardCallId != null && chatMessage.Role == ChatRole.Assistant && richCardsByCallId.TryGetValue(pendingRichCardCallId, out richCard))
                pendingRichCardCallId = null; // Reset after attachment

            // Attach quick replies to assistant message following SetQuickReplies
            List<QuickReply>? quickReplies = null;
            if (pendingQuickRepliesCallId != null && chatMessage.Role == ChatRole.Assistant && quickRepliesByCallId.TryGetValue(pendingQuickRepliesCallId, out quickReplies))
                pendingQuickRepliesCallId = null; // Reset after attachment

            // Add message with both attachments (if present)
            historyMessages.Add(
                MapToMorganaChatMessage(conversationId, agentName, agentCompleted, chatMessage, isLastHistoryMessage, quickReplies, richCard));
        }

        logger.LogInformation(
            "Processed {HistoryMessagesCount} messages (filtered from {AllMessagesCount} raw messages) for {ConversationId}", historyMessages.Count, allMessages.Count, conversationId);

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
            // The argument may arrive in three shapes:
            //  • a JSON string wrapping the array (legacy: when the tool parameter was 'string'),
            //  • a JsonElement that IS a string (legacy, re-hydrated from the session),
            //  • a JsonElement that IS the array itself (current: tool parameter is List<QuickReply>).
            // For the first two we want the inner string; for a native array/object we take the raw JSON.
            string quickRepliesString = quickRepliesValue switch
            {
                string str => str,
                JsonElement { ValueKind: JsonValueKind.String } jsonElement => jsonElement.GetString() ?? "[]",
                JsonElement jsonElement => jsonElement.GetRawText(),
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
            // The argument may arrive as a JSON string wrapping the card, a JsonElement that IS a
            // string (both legacy/current, since SetRichCard still takes a 'string' parameter), or
            // a native JsonElement object (defensive, in case the contract ever switches like QR did).
            string richCardString = richCardsValue switch
            {
                string str => str,
                JsonElement { ValueKind: JsonValueKind.String } jsonElement => jsonElement.GetString() ?? "{}",
                JsonElement jsonElement => jsonElement.GetRawText(),
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