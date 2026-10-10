using System.Security.Cryptography;
using System.Globalization;
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

        // Without a storage path or a key no conversation could be recorded or read back, so the host refuses to start.
        if (string.IsNullOrWhiteSpace(this.options.StoragePath))
            throw new ArgumentException("StoragePath must be configured in appsettings.json");
        if (string.IsNullOrWhiteSpace(this.options.EncryptionKey))
            throw new ArgumentException("EncryptionKey must be configured in appsettings.json");

        // The first conversation's database is created here, so its directory has to exist before any save.
        Directory.CreateDirectory(this.options.StoragePath);

        // The configured key is the AES key itself, decoded once for every row written or read.
        encryptionKey = Convert.FromBase64String(this.options.EncryptionKey);

        // AES-256 takes exactly 32 bytes: a shorter key would not encrypt and a longer one would be silently misread.
        if (encryptionKey.Length != 32)
            throw new ArgumentException("EncryptionKey must be a 256-bit (32-byte) key encoded as Base64");

        logger.LogInformation("{SqLiteConversationPersistenceServiceName} initialized with storage path: {OptionsStoragePath}", nameof(SQLiteConversationPersistenceService), this.options.StoragePath);
    }

    /// <inheritdoc/>
    public async Task EnsureDatabaseInitializedAsync(string conversationId)
    {
        // Opening the file creates the conversation's database when it does not exist yet.
        string connectionString = GetConnectionString(conversationId);
        await using SqliteConnection connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        // The schema is brought to the current version before the first agent or channel writes to it.
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
        // The agent framework's own options read back a session that the same options wrote.
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

            // The history the agent holds in memory, read the way a row is read back so that it can be
            // compared with what the row holds when a rewrite has landed behind the agent.
            IReadOnlyList<ChatMessage> agentMessages = ReadRowMessages(agentSessionJsonString, agentName, conversationId, jsonSerializerOptions);

            // One database per conversation, so every agent of it writes to the same file and nothing
            // here has to filter by conversation.
            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // Cheap on the common path: a pragma read decides, so an already-initialized database
            // pays one query rather than a schema script.
            await EnsureDatabaseInitializedAsync(sqliteConnection);

            // Read and write are one step: a command may be rewriting this row while the agent's turn runs
            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                // The row as it stands now tells whether a rewrite landed behind the agent since its last read.
                await using SqliteCommand readCommand = sqliteConnection.CreateCommand();
                readCommand.Transaction = sqliteTransaction;
                readCommand.CommandText = "SELECT agent_session, is_dirty, agent_message_count FROM morgana WHERE agent_identifier = @agent_identifier;";
                readCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);

                // A row nobody rewrote is replaced by the agent's own session and leaves the dirty state clean.
                string rowToWrite = agentSessionJsonString;
                bool staysDirty = false;
                await using (SqliteDataReader rowReader = await readCommand.ExecuteReaderAsync())
                {
                    // The agent's stored row is read here and its dirty flag says whether a rewrite landed behind the agent since.
                    if (await rowReader.ReadAsync() && rowReader.GetInt64(1) == 1)
                    {
                        // A row rewritten behind the agent holds a history its copy in memory never saw. Everything
                        // the agent had recorded is already in it, folded or verbatim, so only what the agent said
                        // since is appended; its own context state is written as it stands. The row stays dirty,
                        // since the agent's memory is still the history the rewrite replaced.
                        if (!rowReader.IsDBNull(2) && rowReader.GetInt32(2) is var recordedCount && recordedCount <= agentMessages.Count)
                        {
                            // The rewritten history is read out of the row's own encrypted session, so its messages can be merged with the agent's.
                            IReadOnlyList<ChatMessage> rewrittenMessages = ReadRowMessages(Decrypt((byte[])rowReader[0]), agentName, conversationId, jsonSerializerOptions);
                            rowToWrite = ReplaceRowMessages(
                                agentSessionJsonString, [.. rewrittenMessages, .. agentMessages.Skip(recordedCount)], agentName, conversationId, jsonSerializerOptions);
                            staysDirty = true;

                            logger.LogInformation(
                                "Kept the rewrite of {AgentIdentifier}, appending the {Count} message(s) its agent wrote since",
                                agentIdentifier, agentMessages.Count - recordedCount);
                        }
                        else
                        {
                            // Without knowing which of its messages the row already holds, the agent's own history
                            // is the one that loses nothing it said
                            logger.LogWarning(
                                "The rewrite of {AgentIdentifier} cannot be matched to what its agent holds; the agent's history replaces it",
                                agentIdentifier);
                        }
                    }
                }

                // A session holds the whole conversation in clear — user text, tool arguments, results.
                // It is a BLOB on disk for that reason and the key never leaves configuration.
                byte[] encryptedAgentSessionJsonString = Encrypt(rowToWrite);

                // One row per participant: a first save creates it and every later save of the same
                // agent replaces its session while the row keeps its creation date.
                await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
                sqliteCommand.Transaction = sqliteTransaction;
                sqliteCommand.CommandText =
"""
INSERT INTO morgana (agent_identifier, agent_name, agent_session, creation_date, last_update, is_active, is_dirty, agent_message_count)
VALUES (@agent_identifier, @agent_name, @agent_session, @creation_date, @last_update, @is_active, @is_dirty, @agent_message_count)
ON CONFLICT(agent_identifier) DO UPDATE SET
    agent_session = excluded.agent_session, last_update = @last_update, is_active = @is_active,
    is_dirty = @is_dirty, agent_message_count = @agent_message_count;
""";

                // Server time and the same instant for both columns: on an insert they are equal and
                // on the conflict path only last_update is written, so creation_date survives untouched.
                // Invariant like every timestamp here: the active agent is found by sorting last_update as text.
                string utcNow = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

                // is_active is the completion flag inverted: a turn the agent did not close leaves the
                // row active and that is what a resumed conversation reads to find its agent again.
                // The row now accounts for every message the agent holds, which is what a later rewrite
                // behind it has to know.
                sqliteCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);
                sqliteCommand.Parameters.AddWithValue("@agent_name", agentName);
                sqliteCommand.Parameters.AddWithValue("@agent_session", encryptedAgentSessionJsonString);
                sqliteCommand.Parameters.AddWithValue("@creation_date", utcNow);
                sqliteCommand.Parameters.AddWithValue("@last_update", utcNow);
                sqliteCommand.Parameters.AddWithValue("@is_active", isCompleted ? 0 : 1);
                sqliteCommand.Parameters.AddWithValue("@is_dirty", staysDirty ? 1 : 0);
                sqliteCommand.Parameters.AddWithValue("@agent_message_count", agentMessages.Count);

                // The read above and this write share one transaction: a rewrite landing meanwhile is either seen above or waits.
                await sqliteCommand.ExecuteNonQueryAsync();
                await sqliteTransaction.CommitAsync();

                logger.LogInformation("Saved conversation {AgentIdentifier} to database ({Length} bytes encrypted)", agentIdentifier, encryptedAgentSessionJsonString.Length);
            }
            catch
            {
                // A failed write leaves the row exactly as the transaction found it.
                await sqliteTransaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            // The caller decides what a turn that cannot be saved means: the failure is recorded and handed on.
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
        // The agent framework's own options read back a session that the same options wrote.
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        try
        {
            // The conversation id selects the database; the split is at the first dash since the id carries dashes.
            string[] agentIdentifierParts = agentIdentifier.Split('-', 2);
            if (agentIdentifierParts.Length != 2)
                throw new ArgumentException($"Invalid agent_identifier format: '{agentIdentifier}'. Expected format: '{{agent_name}}-{{conversation_id}}'");

            // The part after the first dash is the conversation id, which names the database file.
            string conversationId = agentIdentifierParts[1];

            // One database per conversation: its path both locates the file and tells whether it exists.
            string sqliteConnectionString = GetConnectionString(conversationId);
            string sqliteDbPath = GetDatabasePath(conversationId);

            // A conversation never saved has no database: its agent starts from an empty session and nothing is created here.
            if (!File.Exists(sqliteDbPath))
            {
                logger.LogInformation("Conversation SQLite database for {AgentIdentifier} not found, returning null", agentIdentifier);
                return null;
            }

            // The database is opened only after its file is known to exist, so this lookup never creates one.
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // A database at an earlier schema version gains the dirty column before the read clears it
            await EnsureDatabaseInitializedAsync(sqliteConnection);

            // Reading the row is what brings the agent back in line with it, so the flag is cleared by the same
            // statement: no rewrite can land between the read and the clearing and go unnoticed
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "UPDATE morgana SET is_dirty = 0 WHERE agent_identifier = @agent_identifier RETURNING agent_session;";
            sqliteCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);

            // The row is read once: its BLOB is the session the agent is resurrected from.
            byte[] agentSessionEncryptedJsonString;
            await using (SqliteDataReader sqliteDataReader = await sqliteCommand.ExecuteReaderAsync())
            {
                // An agent that never took a turn here has no row: it starts from an empty session.
                if (!await sqliteDataReader.ReadAsync())
                {
                    logger.LogInformation("Agent session {AgentIdentifier} not found in SQLite database, returning null", agentIdentifier);
                    return null;
                }

                // The encrypted session is taken from the row, the only value the agent is rebuilt from.
                agentSessionEncryptedJsonString = (byte[])sqliteDataReader["agent_session"];
            }

            // The session is decrypted in memory only: nothing readable is written back to disk.
            string agentSessionJsonString = Decrypt(agentSessionEncryptedJsonString);

            // The agent's memory is now exactly this row, which is what a rewrite landing before its next save
            // has to be matched against. A rewrite never touches the count, so one landing in between is still
            // measured against what was read here
            await using SqliteCommand countCommand = sqliteConnection.CreateCommand();
            countCommand.CommandText = "UPDATE morgana SET agent_message_count = @agent_message_count WHERE agent_identifier = @agent_identifier;";
            countCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);
            countCommand.Parameters.AddWithValue("@agent_message_count",
                ReadRowMessages(agentSessionJsonString, agentIdentifierParts[0], conversationId, jsonSerializerOptions).Count);
            await countCommand.ExecuteNonQueryAsync();

            // The agent framework deserializes from a JSON element, so the decrypted text is parsed once here.
            JsonElement agentSessionJsonElement = JsonSerializer.Deserialize<JsonElement>(agentSessionJsonString, jsonSerializerOptions);

            // The agent rebuilds its own session from the row, which is the only way its provider state is restored.
            AgentSession agentSession = await agent.DeserializeSessionAsync(agentSessionJsonElement, jsonSerializerOptions);

            logger.LogInformation("Loaded conversation {AgentIdentifier} from SQLite database ({Length} bytes decrypted)", agentIdentifier, agentSessionEncryptedJsonString.Length);

            // The session the agent continues the conversation on.
            return agentSession;
        }
        catch (Exception ex)
        {
            // An unreadable session must not pass as an empty one: the failure is recorded and handed on.
            logger.LogError(ex, "Failed to load conversation {AgentIdentifier}", agentIdentifier);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> GetMostRecentActiveAgentAsync(string conversationId)
    {
        try
        {
            // Both paths are derived from the conversation id: the connection string opens the database and the path tells whether it was ever created.
            string sqliteConnectionString = GetConnectionString(conversationId);
            string sqliteDbPath = GetDatabasePath(conversationId);

            // A conversation never saved has no active agent. The lookup must not create its database.
            if (!File.Exists(sqliteDbPath))
            {
                logger.LogInformation("SQLite database for conversation {ConversationId} not found", conversationId);
                return null;
            }

            // The active agent is read from this conversation's database, which exists by now.
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            // The agent a resumed conversation wakes up on: active means it left a turn incomplete and
            // the most recently updated of those is the one the user was talking to.
            sqliteCommand.CommandText = "SELECT agent_name FROM morgana WHERE is_active = 1 ORDER BY last_update DESC LIMIT 1;";

            // No active row means every agent closed its turn: the conversation resumes on Morgana.
            object? result = await sqliteCommand.ExecuteScalarAsync();

            string? agentName = result?.ToString();

            logger.LogInformation(
                agentName != null
                    ? $"Most recent agent for conversation {conversationId}: {agentName}"
                    : $"No agents found for conversation {conversationId}");

            // Null tells the caller that no agent is active, so the conversation resumes on Morgana.
            return agentName;
        }
        catch (Exception ex)
        {
            // A lookup that fails is absorbed as "no active agent": resuming on Morgana is safe, a crash on resume is not.
            logger.LogError(ex, "Failed to get most recent agent for conversation {ConversationId}", conversationId);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<MorganaChatMessage[]> GetConversationHistoryAsync(
        string conversationId,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        // Falls back to the agent framework's own options, the same ones that wrote the sessions read here.
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        try
        {
            // The conversation's database is located by its id and its path tells whether it was ever created.
            string sqliteConnectionString = GetConnectionString(conversationId);
            string sqliteDbPath = GetDatabasePath(conversationId);

            // A conversation never saved has no transcript to redraw.
            if (!File.Exists(sqliteDbPath))
            {
                logger.LogInformation("SQLite database for conversation {ConversationId} not found, returning empty history", conversationId);
                return [];
            }

            // The database is opened only once its file is known to exist, so a history read never creates one.
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // Every participant's row is read: the transcript is the merge of all of them.
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "SELECT agent_name, agent_session, is_active FROM morgana ORDER BY creation_date ASC;";

            // Rows come back oldest participant first, so the transcript starts with whoever spoke first.
            await using SqliteDataReader sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();

            // Each message keeps its author and whether that author closed its turn, which the channels render.
            List<(string agentName, bool agentCompleted, ChatMessage message)> allMessages = [];

            // Each row is one participant's whole session.
            while (await sqliteDataReader.ReadAsync())
            {
                // The completion flag is the inverse of is_active, as SaveAgentConversationAsync writes it.
                string agentName = (string)sqliteDataReader["agent_name"];
                bool agentCompleted = (long)sqliteDataReader["is_active"] == 0;

                // Every participant's row is read the same way, the orchestrator's included: what
                // differs between them is what else the row carries, never where the transcript sits.
                byte[] encryptedAgentSessionJsonString = (byte[])sqliteDataReader["agent_session"];
                IReadOnlyList<ChatMessage> chatMessages = ReadRowMessages(
                    Decrypt(encryptedAgentSessionJsonString), agentName, conversationId, jsonSerializerOptions);

                // Add all messages with agent metadata. Filtering of intermediate (non-user-facing)
                // assistant messages happens later in ProcessMessagesForHistory.
                allMessages.AddRange(
                    chatMessages.Select(message => (agentName, agentCompleted, message)));
            }

            // The rows come in the order their agents first appeared while the dialogue interleaves them,
            // so the messages are put back in the order they were said and tool traffic is left out.
            return ProcessMessagesForHistory(
                conversationId, 
                allMessages: [
                    .. allMessages.Where(m => m.message.Role != ChatRole.Tool)
                                  .OrderBy(m => m.message.CreatedAt?.UtcDateTime ?? DateTime.UtcNow)
                ], jsonSerializerOptions);
        }
        catch (Exception ex)
        {
            // A history that cannot be read is an error for the caller: an empty one would look like a new conversation.
            logger.LogError(ex, "Failed to retrieve conversation history for {ConversationId}", conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task AppendOrchestratorMessagesAsync(
        string conversationId,
        IReadOnlyList<ChatMessage> messages)
    {
        // Nothing to record leaves the row and its last_update untouched.
        if (messages.Count == 0)
            return;

        // Morgana's row is keyed like an agent's so every participant is reached the same way.
        string orchestratorIdentifier = $"{Constants.Morgana}-{conversationId}";

        try
        {
            // Morgana's row lives in the same database as the agents' rows, so the same path is used.
            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // The orchestrator speaks before any agent does — the welcome opens the conversation —
            // so this may be the first write the database ever receives.
            await EnsureDatabaseInitializedAsync(sqliteConnection);

            // Read and write are one step: the user's phrase arrives on one path while an answer
            // leaves on another. Either losing the other's line would tear a hole in the dialogue.
            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                // Morgana's transcript so far: the new lines are added to it, never written over it.
                await using SqliteCommand readCommand = sqliteConnection.CreateCommand();
                readCommand.Transaction = sqliteTransaction;
                readCommand.CommandText = "SELECT agent_session FROM morgana WHERE agent_identifier = @agent_identifier;";
                readCommand.Parameters.AddWithValue("@agent_identifier", orchestratorIdentifier);

                // Morgana's stored transcript is read here and a missing row means no line has been spoken yet.
                object? storedRow = await readCommand.ExecuteScalarAsync();
                List<ChatMessage> storedMessages = storedRow is byte[] encryptedStoredRow
                    ? [.. ReadRowMessages(Decrypt(encryptedStoredRow), Constants.Morgana, conversationId)]
                    : [];

                // The first line ever spoken by Morgana starts from an empty transcript.
                storedMessages.AddRange(messages);
                byte[] encryptedRow = Encrypt(WriteRowMessages(storedMessages));

                // Morgana never holds a turn open, so her row is inactive and clean whatever else is recorded.
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
                writeCommand.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));

                // The write lands inside the transaction that read the transcript, so no line appended meanwhile is lost.
                await writeCommand.ExecuteNonQueryAsync();
                await sqliteTransaction.CommitAsync();

                logger.LogInformation(
                    "Appended {Count} orchestrator message(s) to conversation {ConversationId} — {Total} on record",
                    messages.Count, conversationId, storedMessages.Count);
            }
            catch
            {
                // A failed write leaves the row exactly as the transaction found it.
                await sqliteTransaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            // A line that is not recorded would vanish from the history, so the caller is told.
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

        // The participant's row lives in the conversation's own database.
        await using SqliteConnection sqliteConnection = new SqliteConnection(GetConnectionString(conversationId));
        await sqliteConnection.OpenAsync();

        // Only this participant's row is fetched, since its messages are all that it contributes.
        await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();

        // An agent is addressed by the identifier its own turns write under, so the caller names the agent
        // and the conversation rather than having to know how the two are spelled together
        sqliteCommand.CommandText = "SELECT agent_session FROM morgana WHERE agent_identifier = @agent_identifier;";
        sqliteCommand.Parameters.AddWithValue("@agent_identifier", $"{agentName}-{conversationId}");

        // An agent that has never taken a turn here holds no history, which the caller reads as nothing to do
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
        // The same identifier the agent's own turns write under: a row is reached by who wrote it and where
        string agentIdentifier = $"{agentName}-{conversationId}";

        try
        {
            // The participant's row is rewritten in the conversation's own database.
            await using SqliteConnection sqliteConnection = new SqliteConnection(GetConnectionString(conversationId));
            await sqliteConnection.OpenAsync();

            // A database at an earlier schema version gains the dirty column before the rewrite raises it
            await EnsureDatabaseInitializedAsync(sqliteConnection);

            // Read and write are one step: the agent this row belongs to may be writing its own turn
            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                // The row is read inside the transaction, so the check below sees what a rewrite committed before it.
                await using SqliteCommand readCommand = sqliteConnection.CreateCommand();
                readCommand.Transaction = sqliteTransaction;
                readCommand.CommandText = "SELECT agent_session FROM morgana WHERE agent_identifier = @agent_identifier;";
                readCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);

                // An agent with no row has no session to correct: creating one here would invent a
                // participant the conversation never had
                if (await readCommand.ExecuteScalarAsync() is not byte[] encryptedRow)
                    throw new InvalidOperationException($"No row for '{agentName}' in conversation {conversationId}.");

                // What the row holds now decides whether the caller still describes it: an agent only ever
                // appends to its own history, so a row that grew shorter is one this caller read in another life
                string storedRow = Decrypt(encryptedRow);
                IReadOnlyList<ChatMessage> storedMessages = ReadRowMessages(storedRow, agentName, conversationId);
                if (storedMessages.Count < messagesReadCount)
                {
                    // The caller is told to read the row again: nothing is written from a stale picture of it.
                    await sqliteTransaction.RollbackAsync();
                    logger.LogWarning(
                        "The row of '{AgentName}' in conversation {ConversationId} holds {StoredCount} message(s) where the caller read {ReadCount}; leaving it alone",
                        agentName, conversationId, storedMessages.Count, messagesReadCount);
                    return false;
                }

                // A turn the agent saved while the caller was working is kept exactly as the agent wrote it:
                // the caller speaks for the messages it read, never for what was said after them
                List<ChatMessage> rewrittenMessages = [.. messages, .. storedMessages.Skip(messagesReadCount)];

                // Only the history is swapped: an agent's row also carries the context variables its session
                // holds, which belong to the agent and to nobody correcting its transcript
                string rewrittenRow = ReplaceRowMessages(storedRow, rewrittenMessages, agentName, conversationId);

                // The rewrite is written inside the transaction that read the row, so a save landing meanwhile is either seen or waits.
                await using SqliteCommand writeCommand = sqliteConnection.CreateCommand();
                writeCommand.Transaction = sqliteTransaction;

                // The row keeps the state it had: whether the agent is still working on the conversation
                // says nothing about its history having been rewritten. It turns dirty with the rewrite, since
                // the agent's copy in memory still holds the history this write replaced.
                writeCommand.CommandText =
                    "UPDATE morgana SET agent_session = @agent_session, last_update = @now, is_dirty = 1 WHERE agent_identifier = @agent_identifier;";
                writeCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);
                writeCommand.Parameters.AddWithValue("@agent_session", Encrypt(rewrittenRow));
                writeCommand.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));

                // The rewrite and the dirty flag commit together, so the agent can never read the new history as clean.
                await writeCommand.ExecuteNonQueryAsync();
                await sqliteTransaction.CommitAsync();

                logger.LogInformation(
                    "Rewrote the {Count} message(s) of '{AgentName}' in conversation {ConversationId}, keeping {KeptCount} written since",
                    messages.Count, agentName, conversationId, storedMessages.Count - messagesReadCount);
                // The caller's picture matched the row and the rewrite is on record.
                return true;
            }
            catch
            {
                // A failed write leaves the row exactly as the transaction found it.
                await sqliteTransaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            // A rewrite that fails must not be mistaken for a stale picture, which answers false: the caller is told.
            logger.LogError(ex, "Failed to rewrite the messages of '{AgentName}' in conversation {ConversationId}", agentName, conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task SaveChannelMetadataAsync(string conversationId, ChannelMetadata metadata)
    {
        try
        {
            // The handshake goes to the conversation's own database, which is created on first write if needed.
            string sqliteConnectionString = GetConnectionString(conversationId);
            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // First-writer pattern: channel handshake may precede any agent execution,
            // so we must guarantee the schema exists before the upsert.
            await EnsureDatabaseInitializedAsync(sqliteConnection);

            // The conversation has one channel: the handshake is the single row 1, replaced whenever it is repeated.
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
            // SQLite has no boolean: capabilities are stored as 0 or 1 and an absent callback or length as NULL,
            // which is the channel declaring none rather than an empty one.
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

            // Writes the handshake as the conversation's single channel row.
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
            // A conversation without its channel could not deliver a reply, so the failure reaches the caller.
            logger.LogError(ex, "Failed to save channel metadata for conversation {ConversationId}", conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<ChannelMetadata?> LoadChannelMetadataAsync(string conversationId)
    {
        try
        {
            // A conversation never started has no handshake on record.
            string sqliteDbPath = GetDatabasePath(conversationId);
            if (!File.Exists(sqliteDbPath))
            {
                logger.LogInformation("SQLite database for conversation {ConversationId} not found, no persisted channel metadata", conversationId);
                return null;
            }

            // The handshake is the conversation's single channel row, read from its own database.
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

            // A database whose handshake was never recorded reads as a conversation with no channel yet.
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

            // Coordinates and capabilities come back as the channel announced them, already normalised when saved.
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
        // SQLITE_ERROR: a database created before the table existed has no handshake on record.
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            logger.LogInformation("channel_metadata table missing for conversation {ConversationId} (legacy DB), returning null", conversationId);
            return null;
        }
        catch (Exception ex)
        {
            // An unreadable handshake must not pass as an absent one: the failure reaches the caller.
            logger.LogError(ex, "Failed to load channel metadata for conversation {ConversationId}", conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task UpsertSharedVariableAsync(string conversationId, string variableName, object variableValue, string sourceAgentIntent)
    {
        try
        {
            // The variable is written to the conversation's own database.
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
            // Stamped in the same text format as the morgana table, so every last_update in the file reads and sorts alike.
            sqliteCommand.Parameters.AddWithValue("@last_update", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));

            // Zero rows means an earlier writer already claimed the name, which is not an error.
            int rowsAffected = await sqliteCommand.ExecuteNonQueryAsync();

            logger.LogInformation(
                rowsAffected > 0
                    ? "Shared variable '{VariableName}' persisted by '{Source}' for conversation {ConversationId} (first writer)"
                    : "Shared variable '{VariableName}' from '{Source}' for conversation {ConversationId} ignored (already claimed by an earlier writer)",
                variableName, sourceAgentIntent, conversationId);
        }
        catch (Exception ex)
        {
            // A variable that is not shared would be asked of the user again by the next agent, so the caller is told.
            logger.LogError(ex, "Failed to upsert shared variable '{VariableName}' for conversation {ConversationId}", variableName, conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, object>> LoadSharedVariablesAsync(string conversationId)
    {
        try
        {
            // Both paths derive from the conversation id and the second tells whether the database exists.
            string sqliteConnectionString = GetConnectionString(conversationId);
            string sqliteDbPath = GetDatabasePath(conversationId);

            // No DB → no shared variables. The conversation may simply have not started yet;
            // the channel handshake may also be the only thing that has run so far.
            if (!File.Exists(sqliteDbPath))
                return [];

            await using SqliteConnection sqliteConnection = new SqliteConnection(sqliteConnectionString);
            await sqliteConnection.OpenAsync();

            // The whole registry is read: an agent merges every variable at the start of its turn.
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "SELECT variable_name, variable_value FROM shared_context;";

            // The registry is collected by variable name, which is the key each agent merges it under.
            Dictionary<string, object> sharedVariables = new();
            await using SqliteDataReader sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
            while (await sqliteDataReader.ReadAsync())
            {
                string variableName = (string)sqliteDataReader["variable_name"];
                byte[] encrypted = (byte[])sqliteDataReader["variable_value"];

                // Values are stored encrypted, so each one is decrypted before an agent can merge it.
                string decrypted = Decrypt(encrypted);
                JsonElement element = JsonSerializer.Deserialize<JsonElement>(decrypted);

                // Convert back to a "natural" .NET value, so an agent hydrating this variable holds
                // the same shape the writing agent stored.
                object? value = element.ValueKind switch
                {
                    // The stored JSON value is restored to the type that the variable had when an agent registered it.
                    // Text stays text.
                    JsonValueKind.String => element.GetString(),
                    // A whole number is restored as long and any other number as double.
                    JsonValueKind.Number => element.TryGetInt64(out long l) ? (object)l : element.GetDouble(),
                    // The variable held a flag that was set.
                    JsonValueKind.True   => true,
                    // The variable held a flag that was cleared.
                    JsonValueKind.False  => false,
                    // A null value is left as null, which the check below drops.
                    JsonValueKind.Null   => null,
                    _                    => element // arrays/objects: keep as JsonElement
                };
                // A variable stored as null holds no value for an agent to hydrate, so it is left out.
                if (value is not null)
                    sharedVariables[variableName] = value;
            }

            // The registry, with every variable a turn may merge.
            return sharedVariables;
        }
        // SQLITE_ERROR: a database created before the registry existed shares no variables.
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            logger.LogInformation("shared_context table missing for conversation {ConversationId} (legacy DB), returning empty registry", conversationId);
            return [];
        }
        catch (Exception ex)
        {
            // An unreadable registry must not pass as an empty one: agents would ask again what the user already gave.
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
        // The connection string names the file alone: one database per conversation needs no further setting.
        string sqliteDbPath = GetDatabasePath(conversationId);
        return $"Data Source={sqliteDbPath}";
    }

    /// <inheritdoc />
    public async Task<bool> IsDirtyAsync(string agentIdentifier)
    {
        // The conversation id selects the database; the split is at the first dash since the id carries dashes.
        string[] agentIdentifierParts = agentIdentifier.Split('-', 2);
        if (agentIdentifierParts.Length != 2)
            throw new ArgumentException($"Invalid agent_identifier format: '{agentIdentifier}'. Expected format: '{{agent_name}}-{{conversation_id}}'");

        // An agent whose conversation has no database has no row that anybody could have rewritten
        if (!ConversationExists(agentIdentifierParts[1]))
            return false;

        // The connection is opened on the conversation's database, which the check above confirmed exists.
        await using SqliteConnection sqliteConnection = new SqliteConnection(GetConnectionString(agentIdentifierParts[1]));
        await sqliteConnection.OpenAsync();

        // A database at an earlier schema version gains the dirty column, clean, before it is asked
        await EnsureDatabaseInitializedAsync(sqliteConnection);

        // The query asks only for the dirty flag of this agent's row.
        await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "SELECT is_dirty FROM morgana WHERE agent_identifier = @agent_identifier;";
        sqliteCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);

        // No row yet is an agent's first activation, which reads its record anyway
        return await sqliteCommand.ExecuteScalarAsync() is long and 1;
    }

    /// <inheritdoc/>
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
        // A conversation id arrives from the client: characters that are invalid in a file name are replaced
        // so that no id can address a file outside the storage directory.
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
        // The schema version lives in the database file itself, so a conversation is migrated at most once.
        await using SqliteCommand checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "PRAGMA user_version;";
        long currentVersion = (long)(await checkCommand.ExecuteScalarAsync() ?? 0L);

        // A database at the current version pays this one pragma read and nothing else.
        if (currentVersion >= 6)
            return;

        // The script is safe on a database at an earlier version: tables that exist are left intact and
        // only the missing ones are created (shared_context in v4; dust_budget and dust_usage_log in v5).
        await using SqliteCommand schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText =
"""
CREATE TABLE IF NOT EXISTS morgana (
    agent_identifier TEXT PRIMARY KEY NOT NULL,
    agent_name TEXT UNIQUE NOT NULL,
    agent_session BLOB NOT NULL,
    creation_date TEXT NOT NULL,
    last_update TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 0,
    is_dirty INTEGER NOT NULL DEFAULT 0,
    agent_message_count INTEGER NULL
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

        // Version 6 holds is_dirty and agent_message_count in the morgana table, which the script above leaves
        // as an older database has it: the columns are added there, every existing row clean and its count
        // unknown until its agent next reads or writes it, since none was rewritten behind its agent
        foreach ((string column, string declaration) in (IEnumerable<(string, string)>)
                 [("is_dirty", "INTEGER NOT NULL DEFAULT 0"), ("agent_message_count", "INTEGER NULL")])
        {
            // The check counts the column in the table's metadata, so an existing column is never added twice.
            await using SqliteCommand columnCommand = connection.CreateCommand();
            columnCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('morgana') WHERE name = @column;";
            columnCommand.Parameters.AddWithValue("@column", column);
            if ((long)(await columnCommand.ExecuteScalarAsync() ?? 0L) == 0)
            {
                // The column is added with its declaration, so an older database keeps every row and reads it as clean.
                columnCommand.CommandText = $"ALTER TABLE morgana ADD COLUMN {column} {declaration};";
                await columnCommand.ExecuteNonQueryAsync();
            }
        }

        // The version is stamped last, so a schema step that failed is run again at the next open.
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version = 6;";
        await versionCommand.ExecuteNonQueryAsync();

        logger.LogInformation(
            "Initialized database schema v6 for: {GetFileName}", Path.GetFileName(connection.DataSource));
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

        // The blob is built in memory: IV first, then the ciphertext that follows it.
        using MemoryStream msEncrypt = new MemoryStream();

        // The IV isn't secret, only unique — prepending it in the clear is standard practice and
        // is exactly what Decrypt below expects to find at the start of every stored blob.
        msEncrypt.Write(aes.IV, 0, aes.IV.Length);

        // Disposing the writer flushes the final cipher block, so the blob is complete only after this scope.
        using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
        using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
        {
            // The plaintext is written through the encrypting stream, so the ciphertext follows the IV.
            swEncrypt.Write(plaintext);
        }

        // IV and ciphertext, the shape that Decrypt reads.
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

        // The IV that Encrypt prepended is read from the start of the blob; the ciphertext follows it.
        byte[] iv = new byte[aes.IV.Length];
        Array.Copy(ciphertext, 0, iv, 0, iv.Length);
        aes.IV = iv;

        using ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using MemoryStream msDecrypt = new MemoryStream(ciphertext, iv.Length, ciphertext.Length - iv.Length);
        using CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
        using StreamReader srDecrypt = new StreamReader(csDecrypt);

        // The decrypted text is the JSON that the row was written from.
        return srDecrypt.ReadToEnd();
    }

    /// <summary>
    /// Reads the messages a conversation row holds. Returns an empty sequence for a row whose
    /// shape carries none, so one unreadable participant costs its own lines rather than the
    /// whole dialogue.
    /// </summary>
    /// <remarks>
    /// The one place that knows how a row encodes its messages, paired with <see cref="WriteRowMessages"/>.
    /// An agent's row is the agent session the agent resurrects itself from, written by the agent
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
        // Falls back to the agent framework's options, the ones that wrote the row.
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        // The row is parsed once, so each level of its layout can be looked up in turn.
        JsonElement rowElement = JsonSerializer.Deserialize<JsonElement>(rowJson, jsonSerializerOptions);

        // Each level of the layout may be absent in a row that holds no history yet: the participant then
        // contributes no lines and the others are still read.
        if (!rowElement.TryGetProperty(RowStateBagProperty, out JsonElement stateBagElement))
        {
            logger.LogWarning("Conversation row for {AuthorName} missing '{Property}', skipping", authorName, RowStateBagProperty);
            return [];
        }
        // The history state sits one level below the state bag and its absence leaves this participant with no lines.
        if (!stateBagElement.TryGetProperty(RowHistoryStateKey, out JsonElement historyStateElement))
        {
            logger.LogWarning("Conversation row for {AuthorName} missing '{Property}', skipping", authorName, RowHistoryStateKey);
            return [];
        }
        // The messages sit one level below the history state.
        if (!historyStateElement.TryGetProperty(RowMessagesProperty, out JsonElement messagesElement))
        {
            logger.LogWarning("Conversation row for {AuthorName} missing '{Property}', skipping", authorName, RowMessagesProperty);
            return [];
        }

        // A history slot that is present but null is a corrupt row, which is reported rather than read as empty.
        return JsonSerializer.Deserialize<ChatMessage[]>(messagesElement.GetRawText(), jsonSerializerOptions)
               ?? throw new InvalidOperationException(
                   $"Failed to deserialize messages for '{authorName}' in conversation {conversationId}");
    }

    /// <summary>
    /// Builds the row of a participant that holds messages and nothing else, which is the
    /// orchestrator alone. Its output is read back by <see cref="ReadRowMessages"/> exactly as a
    /// agent's row is, so a transcript is assembled without asking who wrote which row.
    /// </summary>
    private static string WriteRowMessages(
        IReadOnlyList<ChatMessage> messages,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        // Falls back to the agent framework's options, the same ones that read the row back.
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        // The nesting mirrors the agent framework's session so that ReadRowMessages finds the messages at one address.
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
    /// everything else untouched. An agent's row carries its context state beside its history, so the
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
        // Falls back to the agent framework's options, so the row is written in the shape it is read in.
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        // The row travels as the session wrote it, so it is taken apart rather than modelled: what an agent
        // keeps beside its history is its own business and must come out the other side untouched
        JsonNode rowNode = JsonNode.Parse(rowJson)
            ?? throw new InvalidOperationException($"The row of '{authorName}' in conversation {conversationId} is empty.");

        // A row without the history the chat provider keeps is one this rewrite cannot be about: writing a
        // history into it would invent state the agent never had
        if (rowNode[RowStateBagProperty]?[RowHistoryStateKey] is not JsonObject historyState)
            throw new InvalidOperationException(
                $"The row of '{authorName}' in conversation {conversationId} holds no history to rewrite.");

        // The messages take the place of the ones that were there, where they were, so the agent reads them
        // back as its own history and finds everything else exactly as it left it
        historyState[RowMessagesProperty] = JsonNode.Parse(JsonSerializer.Serialize(messages, jsonSerializerOptions));
        return rowNode.ToJsonString(jsonSerializerOptions);
    }

    /// <summary>
    /// Processes raw messages from AgentSession into UI-ready MorganaChatMessage array.
    /// Handles message filtering, the buttons and card each turn delivered and chronological ordering.
    /// </summary>
    private MorganaChatMessage[] ProcessMessagesForHistory(
        string conversationId,
        List<(string agentName, bool agentCompleted, ChatMessage message)> allMessages,
        JsonSerializerOptions jsonSerializerOptions)
    {
        // The transcript the channels redraw is collected here, one entry per rendered message.
        List<MorganaChatMessage> historyMessages = [];

        // The agents whose history carries at least one user-facing marker. Their intermediate assistant
        // messages are the tool-use scratchpad and stay out of the transcript on resume. An agent with no
        // marker has every assistant message rendered, since nothing tells its answers from its scratchpad.
        HashSet<string> markedAgents =
        [
            .. allMessages
                .Where(m => m.message.Role == ChatRole.Assistant
                            && m.message.AdditionalProperties?.ContainsKey(Constants.MessageProperties.UserFacing) == true)
                .Select(m => m.agentName)
        ];

        // Each stored message is turned into a transcript entry only if the user is meant to read it, whoever wrote it.
        foreach ((string agentName, bool agentCompleted, ChatMessage chatMessage) in allMessages)
        {
            // Tool traffic is the agent's working, not a line of the dialogue.
            if (chatMessage.Role == ChatRole.Tool)
                continue;

            // The agent holds this phrase only so its model could read it: no agent was active when it
            // arrived, so Morgana saved it on her own side and the user reads it once, from there.
            if (chatMessage.Role == ChatRole.User
                && chatMessage.AdditionalProperties?.ContainsKey(Constants.MessageProperties.ContextOnly) == true)
                continue;

            // Decide whether this assistant message is the user-facing one for its turn. The
            // marker is set by MorganaAgent at end-of-turn on the last assistant message that
            // actually carries text — see MorganaAgent.ExecuteAgentAsync.
            bool isUserFacing = chatMessage.Role == ChatRole.Assistant
                             && chatMessage.AdditionalProperties?.ContainsKey(Constants.MessageProperties.UserFacing) == true;

            // An unmarked assistant message of a marking agent is the tool-use scratchpad of a turn.
            if (chatMessage.Role == ChatRole.Assistant && markedAgents.Contains(agentName) && !isUserFacing)
                continue;

            // A message with no text shows nothing, so it takes no place in the transcript.
            string messageText = ExtractTextFromMessage(chatMessage);
            if (string.IsNullOrWhiteSpace(messageText))
                continue;

            // The buttons and card that the turn delivered, recorded on the message that they arrived with.
            List<QuickReply>? quickReplies = isUserFacing
                ? TryReadRecorded<List<QuickReply>>(chatMessage, Constants.MessageProperties.TurnQuickReplies, jsonSerializerOptions)
                : null;
            RichCard? richCard = isUserFacing
                ? TryReadRecorded<RichCard>(chatMessage, Constants.MessageProperties.TurnRichCard, jsonSerializerOptions)
                : null;

            // The message joins the transcript as the channels render it; the last one is marked below.
            historyMessages.Add(
                MapToMorganaChatMessage(conversationId, agentName, agentCompleted, chatMessage, isLastHistoryMessage: false, quickReplies, richCard));
        }

        // The last message that is rendered carries the flag, so that its buttons and card stay interactive
        // after a resume or refresh instead of being frozen because the raw session tail was empty.
        if (historyMessages.Count > 0)
        {
            // The newest rendered message is flagged, so a client catching up knows where the history ends.
            int lastIndex = historyMessages.Count - 1;
            historyMessages[lastIndex] = historyMessages[lastIndex] with { IsLastHistoryMessage = true };
        }

        logger.LogInformation(
            "Processed {HistoryMessagesCount} messages (filtered from {AllMessagesCount} raw messages) for {ConversationId}", historyMessages.Count, allMessages.Count, conversationId);

        // The transcript the channel redraws.
        return [.. historyMessages];
    }

    /// <summary>
    /// Reads a value that a turn recorded on its user-facing message as JSON; null when it recorded none
    /// or what it recorded no longer reads (graceful degradation: the text is still shown).
    /// </summary>
    private T? TryReadRecorded<T>(ChatMessage chatMessage, string propertyName, JsonSerializerOptions jsonSerializerOptions) where T : class
    {
        // A turn that delivered no buttons or card recorded nothing under this name.
        string? recorded = TryGetRecordedString(chatMessage, propertyName);
        if (string.IsNullOrWhiteSpace(recorded))
            return null;

        try
        {
            // The recorded value is read back into its type and a value that no longer matches it is not returned.
            return JsonSerializer.Deserialize<T>(recorded, jsonSerializerOptions);
        }
        catch (JsonException ex)
        {
            // Buttons and card are an embellishment of the reply: the user still reads the text without them.
            logger.LogWarning(ex, "Failed to read the recorded {PropertyName} of a turn", propertyName);
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
    /// text (user turns) fall through to their own text content.
    /// </remarks>
    private string ExtractTextFromMessage(ChatMessage chatMessage)
    {
        // What the user actually read, whole, when the turn recorded it.
        string? turnText = TryGetRecordedString(chatMessage, Constants.MessageProperties.TurnText);
        if (!string.IsNullOrWhiteSpace(turnText))
            return turnText;

        // Nothing a transcript could show. A message stripped of its content is not a turn.
        if (chatMessage.Contents == null || chatMessage.Contents.Count == 0)
            return string.Empty;

        // Every text fragment joined into one line: a model may split an answer across several content
        // parts, while a transcript shows one message per turn. Text alone, because that is what a
        // transcript is: an image or a data part would need a shape on MorganaChatMessage that no channel asks for.
        return string.Join(" ", chatMessage.Contents
            .OfType<TextContent>()
            .Where(tc => !string.IsNullOrEmpty(tc.Text))
            .Select(tc => tc.Text.Trim()));
    }

    /// <summary>
    /// Reads a string that a turn recorded on a message under <paramref name="propertyName"/>; <c>null</c> when absent.
    /// </summary>
    /// <remarks>
    /// A session's properties come back from the encrypted round trip as loosely-typed JSON, so the same
    /// value has two shapes depending on whether the session was ever persisted.
    /// </remarks>
    private static string? TryGetRecordedString(ChatMessage chatMessage, string propertyName)
    {
        // Absent on a user turn, on a turn that recorded nothing under this name or on an older session.
        if (chatMessage.AdditionalProperties?.TryGetValue(propertyName, out object? value) != true)
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

        // A transcript knows only who spoke: everything not the user is Morgana, whichever agent answered.
        ChatMessageType messageType = chatMessage.Role == ChatRole.User
            ? ChatMessageType.User
            : ChatMessageType.Assistant;

        // The name a channel prints beside the bubble. The pipeline speaking in its own voice is plain
        // "Morgana"; an agent is named beside it, so a user sees one assistant with several competences.
        string displayAgentName = chatMessage.Role == ChatRole.User
            ? "User"
            : string.IsNullOrEmpty(agentName) || agentName.Equals(Constants.Morgana, StringComparison.OrdinalIgnoreCase)
                ? Constants.Morgana
                : $"Morgana ({char.ToUpperInvariant(agentName[0])}{agentName[1..]})";

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