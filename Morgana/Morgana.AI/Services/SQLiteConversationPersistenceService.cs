using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// SQLite-based persistence with AES-256 encryption, one database per conversation.
/// Stores agent sessions per agent with primary key agent_identifier; single-threaded (one active agent).
/// IV prepended to ciphertext; StoragePath + EncryptionKey (base64, 256-bit) configured in appsettings.json.
/// </summary>
public class SQLiteConversationPersistenceService : IConversationPersistenceService
{
    /// <summary>
    /// Logger for schema migrations, session save/load and decryption failures.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>Where the agent framework files provider state inside a serialized session.</summary>
    private const string RowStateBagProperty = "stateBag";

    /// <summary>The history provider's own slot in that state, named after the provider itself.</summary>
    private const string RowHistoryStateKey = nameof(MorganaChatHistoryProvider);

    /// <summary>The transcript inside that slot.</summary>
    private const string RowMessagesProperty = "messages";

    /// <summary>
    /// Persistence configuration from <c>Morgana:ConversationPersistence</c>: <c>StoragePath</c>
    /// (where the per-conversation database files live) and the configured encryption key.
    /// </summary>
    private readonly ConversationPersistenceOptions options;

    /// <summary>
    /// The 32-byte AES-256 key decoded from the configured base64 string, validated for length at
    /// construction. Used for every <c>agent_session</c> BLOB, with the IV prepended to ciphertext.
    /// </summary>
    private readonly byte[] encryptionKey;

    /// <summary>Validates StoragePath/EncryptionKey are configured and ensures the storage directory exists.</summary>
    /// <exception cref="ArgumentException">StoragePath or EncryptionKey are not configured.</exception>
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
            // Split at the FIRST dash only: a conversation id is a GUID and carries dashes of its own,
            // so an unbounded split would cut it apart and address the wrong database.
            string[] agentIdentifierParts = agentIdentifier.Split('-', 2);
            if (agentIdentifierParts.Length != 2)
                throw new ArgumentException($"Invalid agent_identifier format: '{agentIdentifier}'. Expected format: '{{agent_name}}-{{conversation_id}}'");
            string agentName = agentIdentifierParts[0];
            string conversationId = agentIdentifierParts[1];

            // Serialized by the agent itself, not by this layer: the session's shape belongs to the
            // agent framework and only it knows how to write one that can be read back.
            JsonElement agentSessionJsonElement = await agent.SerializeSessionAsync(agentSession, jsonSerializerOptions);
            string agentSessionJsonString = JsonSerializer.Serialize(agentSessionJsonElement, jsonSerializerOptions);

            // A session holds the whole conversation in clear — user text, tool arguments, results.
            // It is a BLOB on disk for that reason and the key never leaves configuration.
            byte[] encryptedAgentSessionJsonString = Encrypt(agentSessionJsonString);

            // One database per conversation, so every agent of it writes to the same file and nothing
            // here has to filter by conversation.
            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // Cheap on the common path: a pragma read decides, so an already-initialized database
            // pays one query rather than a schema script.
            await EnsureDatabaseInitializedAsync(sqliteConnection);

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

                // Server time and the same instant for both columns: on an insert they are equal and
                // on the conflict path only last_update is written, so creation_date survives untouched.
                string utcNow = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

                // is_active is the completion flag inverted: a turn the agent did not close leaves the
                // row active and that is what a resumed conversation reads to find its agent again.
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
            // The agent a resumed conversation wakes up on: active means it left a turn incomplete and
            // the most recently updated of those is the one the user was talking to.
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

                // Every participant's row is read the same way, the orchestrator's included: what
                // differs between them is what else the row carries, never where the transcript sits.
                byte[] encryptedAgentSessionJsonString = (byte[])sqliteDataReader["agent_session"];
                IReadOnlyList<ChatMessage> chatMessages = ReadRowMessages(
                    Decrypt(encryptedAgentSessionJsonString), agentName, conversationId, jsonSerializerOptions);

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
            return ProcessMessagesForHistory(
                conversationId, 
                allMessages: [
                    .. allMessages.Where(m => m.message.Role != ChatRole.Tool)
                                  .OrderBy(m => m.message.CreatedAt?.UtcDateTime ?? DateTime.UtcNow)
                ], jsonSerializerOptions);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve conversation history for {ConversationId}", conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task AppendOrchestratorMessagesAsync(
        string conversationId,
        IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
            return;

        string orchestratorIdentifier = $"{Constants.Morgana}-{conversationId}";

        try
        {
            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // The orchestrator speaks before any desk does — the welcome opens the conversation —
            // so this may be the first write the database ever receives.
            await EnsureDatabaseInitializedAsync(sqliteConnection);

            // Read and write are one step: the user's phrase arrives on one path while an answer
            // leaves on another. Either losing the other's line would tear a hole in the dialogue.
            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                await using SqliteCommand readCommand = sqliteConnection.CreateCommand();
                readCommand.Transaction = sqliteTransaction;
                readCommand.CommandText = "SELECT agent_session FROM morgana WHERE agent_identifier = @agent_identifier;";
                readCommand.Parameters.AddWithValue("@agent_identifier", orchestratorIdentifier);

                object? storedRow = await readCommand.ExecuteScalarAsync();
                List<ChatMessage> storedMessages = storedRow is byte[] encryptedStoredRow
                    ? [.. ReadRowMessages(Decrypt(encryptedStoredRow), Constants.Morgana, conversationId)]
                    : [];

                storedMessages.AddRange(messages);
                byte[] encryptedRow = Encrypt(WriteRowMessages(storedMessages));

                await using SqliteCommand writeCommand = sqliteConnection.CreateCommand();
                writeCommand.Transaction = sqliteTransaction;
                writeCommand.CommandText =
"""
INSERT INTO morgana (agent_identifier, agent_name, agent_session, creation_date, last_update, is_active)
VALUES (@agent_identifier, @agent_name, @agent_session, @now, @now, 0)
ON CONFLICT(agent_identifier) DO UPDATE SET
    agent_session = excluded.agent_session, last_update = @now;
""";
                writeCommand.Parameters.AddWithValue("@agent_identifier", orchestratorIdentifier);
                writeCommand.Parameters.AddWithValue("@agent_name", Constants.Morgana);
                writeCommand.Parameters.AddWithValue("@agent_session", encryptedRow);
                writeCommand.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

                await writeCommand.ExecuteNonQueryAsync();
                await sqliteTransaction.CommitAsync();

                logger.LogInformation(
                    "Appended {Count} orchestrator message(s) to conversation {ConversationId} — {Total} on record",
                    messages.Count, conversationId, storedMessages.Count);
            }
            catch
            {
                await sqliteTransaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to append orchestrator messages to conversation {ConversationId}", conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChatMessage>> LoadParticipantMessagesAsync(string conversationId, string agentName)
    {
        // A conversation nobody has spoken in has no database, which is not a failure to report
        if (!ConversationExists(conversationId))
            return [];

        await using SqliteConnection sqliteConnection = new SqliteConnection(GetConnectionString(conversationId));
        await sqliteConnection.OpenAsync();

        await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();

        // A desk is addressed by the identifier its own turns write under, so the caller names the desk
        // and the conversation rather than having to know how the two are spelled together
        sqliteCommand.CommandText = "SELECT agent_session FROM morgana WHERE agent_identifier = @agent_identifier;";
        sqliteCommand.Parameters.AddWithValue("@agent_identifier", $"{agentName}-{conversationId}");

        // A desk that has never taken a turn here holds no history, which the caller reads as nothing to do
        object? storedRow = await sqliteCommand.ExecuteScalarAsync();
        return storedRow is byte[] encryptedRow
            ? ReadRowMessages(Decrypt(encryptedRow), agentName, conversationId)
            : [];
    }

    /// <inheritdoc/>
    public async Task<bool> SaveParticipantMessagesAsync(
        string conversationId,
        string agentName,
        IReadOnlyList<ChatMessage> messages,
        int messagesReadCount)
    {
        // The same identifier the desk's own turns write under: a row is reached by who wrote it and where
        string agentIdentifier = $"{agentName}-{conversationId}";

        try
        {
            await using SqliteConnection sqliteConnection = new SqliteConnection(GetConnectionString(conversationId));
            await sqliteConnection.OpenAsync();

            // Read and write are one step: the desk this row belongs to may be writing its own turn
            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                await using SqliteCommand readCommand = sqliteConnection.CreateCommand();
                readCommand.Transaction = sqliteTransaction;
                readCommand.CommandText = "SELECT agent_session FROM morgana WHERE agent_identifier = @agent_identifier;";
                readCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);

                // A desk with no row has no session to correct: creating one here would invent a
                // participant the conversation never had
                if (await readCommand.ExecuteScalarAsync() is not byte[] encryptedRow)
                    throw new InvalidOperationException($"No row for '{agentName}' in conversation {conversationId}.");

                // What the row holds now decides whether the caller still describes it: a desk only ever
                // appends to its own history, so a row that grew shorter is one this caller read in another life
                string storedRow = Decrypt(encryptedRow);
                IReadOnlyList<ChatMessage> storedMessages = ReadRowMessages(storedRow, agentName, conversationId);
                if (storedMessages.Count < messagesReadCount)
                {
                    await sqliteTransaction.RollbackAsync();
                    logger.LogWarning(
                        "The row of '{AgentName}' in conversation {ConversationId} holds {StoredCount} message(s) where the caller read {ReadCount}; leaving it alone",
                        agentName, conversationId, storedMessages.Count, messagesReadCount);
                    return false;
                }

                // A turn the desk saved while the caller was working is kept exactly as the desk wrote it:
                // the caller speaks for the messages it read, never for what was said after them
                List<ChatMessage> rewrittenMessages = [.. messages, .. storedMessages.Skip(messagesReadCount)];

                // Only the history is swapped: a desk's row also carries the context variables its session
                // holds, which belong to the desk and to nobody correcting its transcript
                string rewrittenRow = ReplaceRowMessages(storedRow, rewrittenMessages, agentName, conversationId);

                await using SqliteCommand writeCommand = sqliteConnection.CreateCommand();
                writeCommand.Transaction = sqliteTransaction;

                // The row keeps the state it had: whether the desk is still working on the conversation
                // says nothing about its history having been rewritten
                writeCommand.CommandText =
                    "UPDATE morgana SET agent_session = @agent_session, last_update = @now WHERE agent_identifier = @agent_identifier;";
                writeCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);
                writeCommand.Parameters.AddWithValue("@agent_session", Encrypt(rewrittenRow));
                writeCommand.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

                await writeCommand.ExecuteNonQueryAsync();
                await sqliteTransaction.CommitAsync();

                logger.LogInformation(
                    "Rewrote the {Count} message(s) of '{AgentName}' in conversation {ConversationId}, keeping {KeptCount} written since",
                    messages.Count, agentName, conversationId, storedMessages.Count - messagesReadCount);
                return true;
            }
            catch
            {
                await sqliteTransaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to rewrite the messages of '{AgentName}' in conversation {ConversationId}", agentName, conversationId);
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

            // SQLite has no boolean and no int32: every flag comes back as a long and an absent length
            // as DBNull, which is the channel declaring no budget rather than a budget of zero.
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
            // Shared variables are typically short strings (e.g. customerCode), so the overhead is
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
                    _                    => element // arrays/objects: keep as JsonElement
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
        => File.Exists(GetDatabasePath(conversationId));

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
        // A fresh random IV every single call, never reused across rows or across saves of the
        // same row: CBC mode leaks patterns between ciphertexts that share both key and IV, so
        // reuse would be the actual vulnerability, not just a decrypt-time inconvenience.
        aes.GenerateIV();

        using ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using MemoryStream msEncrypt = new MemoryStream();

        // The IV isn't secret, only unique — prepending it in the clear is standard practice and
        // is exactly what Decrypt below expects to find at the start of every stored blob.
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
    /// Reads the messages a conversation row holds. Returns an empty sequence for a row whose
    /// shape carries none, so one unreadable participant costs its own lines rather than the
    /// whole dialogue.
    /// </summary>
    /// <remarks>
    /// The one place that knows how a row encodes its messages, paired with <see cref="WriteRowMessages"/>.
    /// A desk's row is the agent session the desk resurrects itself from, written by the agent
    /// framework; the orchestrator's carries the same outer shape with nothing but messages inside.
    /// Both are read here, so the framework's layout is known at one address instead of wherever a
    /// caller happens to need a transcript.
    /// </remarks>
    private IReadOnlyList<ChatMessage> ReadRowMessages(
        string rowJson,
        string authorName,
        string conversationId,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        JsonElement rowElement = JsonSerializer.Deserialize<JsonElement>(rowJson, jsonSerializerOptions);

        if (!rowElement.TryGetProperty(RowStateBagProperty, out JsonElement stateBagElement))
        {
            logger.LogWarning("Conversation row for {AuthorName} missing '{Property}', skipping", authorName, RowStateBagProperty);
            return [];
        }
        if (!stateBagElement.TryGetProperty(RowHistoryStateKey, out JsonElement historyStateElement))
        {
            logger.LogWarning("Conversation row for {AuthorName} missing '{Property}', skipping", authorName, RowHistoryStateKey);
            return [];
        }
        if (!historyStateElement.TryGetProperty(RowMessagesProperty, out JsonElement messagesElement))
        {
            logger.LogWarning("Conversation row for {AuthorName} missing '{Property}', skipping", authorName, RowMessagesProperty);
            return [];
        }

        return JsonSerializer.Deserialize<ChatMessage[]>(messagesElement.GetRawText(), jsonSerializerOptions)
               ?? throw new InvalidOperationException(
                   $"Failed to deserialize messages for '{authorName}' in conversation {conversationId}");
    }

    /// <summary>
    /// Builds the row of a participant that holds messages and nothing else, which is the
    /// orchestrator alone. Its output is read back by <see cref="ReadRowMessages"/> exactly as a
    /// desk's row is, so a transcript is assembled without asking who wrote which row.
    /// </summary>
    private static string WriteRowMessages(
        IReadOnlyList<ChatMessage> messages,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        return JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                [RowStateBagProperty] = new Dictionary<string, object>
                {
                    [RowHistoryStateKey] = new Dictionary<string, object>
                    {
                        [RowMessagesProperty] = messages
                    }
                }
            },
            jsonSerializerOptions);
    }

    /// <summary>
    /// Returns <paramref name="rowJson"/> with its history replaced by <paramref name="messages"/> and
    /// everything else untouched. A desk's row carries its context state beside its history, so the
    /// history is swapped where it sits rather than the row being rebuilt around it.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the row does not hold a history to replace.</exception>
    private static string ReplaceRowMessages(
        string rowJson,
        IReadOnlyList<ChatMessage> messages,
        string authorName,
        string conversationId,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        // The row travels as the session wrote it, so it is taken apart rather than modelled: what a desk
        // keeps beside its history is its own business and must come out the other side untouched
        JsonNode rowNode = JsonNode.Parse(rowJson)
            ?? throw new InvalidOperationException($"The row of '{authorName}' in conversation {conversationId} is empty.");

        // A row without the history the chat provider keeps is one this rewrite cannot be about: writing a
        // history into it would invent state the desk never had
        if (rowNode[RowStateBagProperty]?[RowHistoryStateKey] is not JsonObject historyState)
            throw new InvalidOperationException(
                $"The row of '{authorName}' in conversation {conversationId} holds no history to rewrite.");

        // The messages take the place of the ones that were there, where they were, so the desk reads them
        // back as its own history and finds everything else exactly as it left it
        historyState[RowMessagesProperty] = JsonNode.Parse(JsonSerializer.Serialize(messages, jsonSerializerOptions));
        return rowNode.ToJsonString(jsonSerializerOptions);
    }

    /// <summary>
    /// Processes raw messages from AgentSession into UI-ready MorganaChatMessage array.
    /// Handles quick reply extraction, message filtering and chronological ordering.
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
                .Where(fc => fc.Name == Constants.Tools.SetRichCard) ?? [])
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
                .Where(fc => fc.Name == Constants.Tools.SetQuickReplies) ?? [])
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

        // Set of agents whose persisted history carries at least one user_facing marker. Within
        // those agents we filter out intermediate (non-user-facing) assistant messages so the
        // tool-use scratchpad doesn't appear in the rendered transcript on resume. Agents whose
        // sessions have no marker (e.g. persisted before this feature shipped) follow the legacy
        // path: every assistant message is rendered, just like before.
        HashSet<string> markedAgents =
        [
            .. allMessages
                .Where(m => m.message.Role == ChatRole.Assistant
                            && m.message.AdditionalProperties?.ContainsKey(Constants.MessageProperties.UserFacing) == true)
                .Select(m => m.agentName)
        ];

        // NOTE: MorganaChatReducer annotates the anchor message with
        // `AdditionalProperties["__summary__"]` when it reduces the view for the LLM.
        // That anchor is a real, user-visible turn — it must NOT be filtered out here,
        // otherwise quick replies/rich cards attached to it leak into the next turn.

        foreach ((string agentName, bool agentCompleted, ChatMessage chatMessage) in allMessages)
        {
            bool chatMessageHasToolCalls = false;

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

                // The agent holds this phrase only so its model could read it: no agent was active
                // when it arrived, so Morgana saved it on her own side. Closing the pending
                // attachments above still applies — the turn happened either way — but the phrase
                // itself is taken from where it was saved, so the user reads it once.
                if (chatMessage.AdditionalProperties?.ContainsKey(Constants.MessageProperties.ContextOnly) == true)
                    continue;
            }

            // Check for SetRichCard function call
            FunctionCallContent? setRichCardCall = chatMessage.Contents?
                .OfType<FunctionCallContent>()
                .FirstOrDefault(fc => fc.Name == Constants.Tools.SetRichCard);
            if (setRichCardCall != null)
            {
                pendingRichCardCallId = setRichCardCall.CallId;
                chatMessageHasToolCalls = true;
            }

            // Check for SetQuickReplies function call
            FunctionCallContent? setQuickRepliesCall = chatMessage.Contents?
                .OfType<FunctionCallContent>()
                .FirstOrDefault(fc => fc.Name == Constants.Tools.SetQuickReplies);
            if (setQuickRepliesCall != null)
            {
                pendingQuickRepliesCallId = setQuickRepliesCall.CallId;
                chatMessageHasToolCalls = true;
            }

            // Decide whether this assistant message is the user-facing one for its turn. The
            // marker is set by MorganaAgent at end-of-turn on the last assistant message that
            // actually carries text — see MorganaAgent.ExecuteAgentAsync.
            bool isUserFacing = chatMessage.Role == ChatRole.Assistant
                             && chatMessage.AdditionalProperties?.ContainsKey(Constants.MessageProperties.UserFacing) == true;

            // Tool-call messages are normally skipped — their widgets attach to a later,
            // text-bearing assistant message. Exception: the user-facing message itself, when a
            // model (typically Haiku-class) closes the turn carrying BOTH text and the tool call
            // in one message — falling through here keeps text and widgets in one bubble.
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
                MapToMorganaChatMessage(conversationId, agentName, agentCompleted, chatMessage, isLastHistoryMessage: false, quickReplies, richCard));
        }

        // Mark the last emitted message (the one actually rendered in the UI) as the last
        // history message, so trailing quick replies/rich cards on it stay interactive after
        // a resume/refresh instead of being frozen because the raw session tail was empty.
        if (historyMessages.Count > 0)
        {
            int lastIndex = historyMessages.Count - 1;
            historyMessages[lastIndex] = historyMessages[lastIndex] with { IsLastHistoryMessage = true };
        }

        logger.LogInformation(
            "Processed {HistoryMessagesCount} messages (filtered from {AllMessagesCount} raw messages) for {ConversationId}", historyMessages.Count, allMessages.Count, conversationId);

        return [.. historyMessages];
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

            List<QuickReply>? quickReplies = JsonSerializer.Deserialize<List<QuickReply>>(quickRepliesString, jsonSerializerOptions);
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

            return JsonSerializer.Deserialize<RichCard>(richCardString, jsonSerializerOptions);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse rich card from function arguments");
            return null;
        }
    }

    /// <summary>
    /// Text to render for a ChatMessage: the turn's delivered reply when the message carries one,
    /// otherwise its own TextContent blocks concatenated.
    /// </summary>
    /// <remarks>
    /// The recorded turn text wins because it is what the user read. A turn that called a tool wrote
    /// its answer across several assistant messages and only the last survives the filter above, so
    /// extracting from this message alone would silently drop everything written before the tool call
    /// — see <see cref="Constants.MessageProperties.TurnText"/>. Messages that carry no recorded turn
    /// text (user turns, sessions persisted before this shipped) fall through to the original path.
    /// </remarks>
    private string ExtractTextFromMessage(ChatMessage chatMessage)
    {
        // What the user actually read, whole, when the turn recorded it.
        string? turnText = TryGetRecordedTurnText(chatMessage);
        if (!string.IsNullOrWhiteSpace(turnText))
            return turnText;

        // Nothing a transcript could show. A message stripped of its content is not a turn.
        if (chatMessage.Contents == null || chatMessage.Contents.Count == 0)
            return string.Empty;

        // Every text fragment joined into one line: a model may split an answer across several content
        // parts, while a transcript shows one message per turn.
        return string.Join(" ", chatMessage.Contents
            .OfType<TextContent>()
            .Where(tc => !string.IsNullOrEmpty(tc.Text))
            .Select(tc => tc.Text.Trim()));

        // Text alone, because that is what a transcript is. An image or a data part would need a shape
        // on MorganaChatMessage that no channel is asking for yet.
    }

    /// <summary>
    /// Reads <see cref="Constants.MessageProperties.TurnText"/> off a message, or <c>null</c> when absent.
    /// </summary>
    /// <remarks>
    /// A session's properties come back from the encrypted round trip as loosely-typed JSON, so the same
    /// value has two shapes depending on whether the session was ever persisted.
    /// </remarks>
    private static string? TryGetRecordedTurnText(ChatMessage chatMessage)
    {
        // Absent on a user turn or on a session persisted before turn text was ever recorded.
        if (chatMessage.AdditionalProperties?.TryGetValue(Constants.MessageProperties.TurnText, out object? value) != true)
            return null;

        // A session still in memory hands back what was stored; one reloaded from the database hands
        // back JSON. Anything else is treated as absent rather than trusted, which leaves the caller on
        // its own extraction path instead of on a value nobody wrote.
        return value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null
        };
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
        // What this turn actually said, which is not always the text of this one message.
        string messageText = ExtractTextFromMessage(chatMessage);

        // A transcript knows only who spoke: everything not the user is Morgana, whichever desk answered.
        ChatMessageType messageType = chatMessage.Role == ChatRole.User
            ? ChatMessageType.User
            : ChatMessageType.Assistant;

        // The name a channel prints beside the bubble. The pipeline speaking in its own voice is plain
        // "Morgana"; a desk is named beside it, so a user sees one assistant with several competences.
        string displayAgentName = chatMessage.Role == ChatRole.User
            ? "User"
            : string.IsNullOrEmpty(agentName) || agentName.Equals(Constants.Morgana, StringComparison.OrdinalIgnoreCase)
                ? Constants.Morgana
                : $"Morgana ({char.ToUpper(agentName[0])}{agentName[1..]})";

        // The wire shape a channel renders. Nothing downstream reads the original message again, so
        // everything a transcript needs has been decided by this line.
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