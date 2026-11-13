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
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
using Morgana.Contracts;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

/// <summary>
/// Keeps each conversation in a SQLite database of its own: one AES-256 encrypted row per participant, the handshake, the shared context and the ledgers.
/// The key and the folder come from <c>Morgana:ConversationPersistence</c>; every encrypted blob carries its IV in front.
/// </summary>
public class SQLiteConversationPersistenceService : IConversationPersistenceService
{
    /// <summary>Logs the schema creations, the saves, the loads and the failures to read a row.</summary>
    private readonly ILogger logger;

    /// <summary>Where the agent framework files provider state inside a serialized session.</summary>
    private const string RowStateBagProperty = "stateBag";

    /// <summary>The history provider's own slot in that state, named after the provider itself.</summary>
    private const string RowHistoryStateKey = nameof(MorganaChatHistoryProvider);

    /// <summary>The transcript inside that slot.</summary>
    private const string RowMessagesProperty = "messages";

    /// <summary>
    /// The format of every timestamp stored as text: fixed-width ISO-8601, invariant.
    /// The active agent is found by sorting last_update as text, which this format sorts in time.
    /// </summary>
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    /// <summary>The configuration of <c>Morgana:ConversationPersistence</c>: the folder of the databases and the encryption key.</summary>
    private readonly ConversationPersistenceOptions options;

    /// <summary>The 32-byte AES-256 key decoded from the configured base64, which encrypts every row and every shared variable.</summary>
    private readonly byte[] encryptionKey;

    /// <summary>Validates StoragePath/EncryptionKey are configured and ensures the storage directory exists.</summary>
    /// <exception cref="ArgumentException">StoragePath or EncryptionKey are not configured.</exception>
    public SQLiteConversationPersistenceService(
        IOptions<ConversationPersistenceOptions> options,
        ILogger logger)
    {
        this.logger = logger;
        this.options = options.Value;

        // A deployment that names no folder or no key cannot keep a conversation, so it does not start.
        if (string.IsNullOrWhiteSpace(this.options.StoragePath))
            throw new ArgumentException("StoragePath must be configured in appsettings.json");
        if (string.IsNullOrWhiteSpace(this.options.EncryptionKey))
            throw new ArgumentException("EncryptionKey must be configured in appsettings.json");

        // The folder exists before the first conversation creates its file there.
        Directory.CreateDirectory(this.options.StoragePath);

        // The key is configured as base64 and must be exactly the 256 bits AES-256 takes.
        encryptionKey = Convert.FromBase64String(this.options.EncryptionKey);
        if (encryptionKey.Length != 32)
            throw new ArgumentException("EncryptionKey must be a 256-bit (32-byte) key encoded as Base64");

        logger.LogInformation("{SqLiteConversationPersistenceServiceName} initialized with storage path: {OptionsStoragePath}", nameof(SQLiteConversationPersistenceService), this.options.StoragePath);
    }

    /// <inheritdoc/>
    public async Task EnsureDatabaseInitializedAsync(string conversationId)
    {
        // Opening the database is what creates it and brings it to the current version.
        await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);
    }

    /// <summary>Opens the conversation's database at the current schema version, creating it when absent; the caller disposes the connection.</summary>
    public async Task<SqliteConnection> OpenDatabaseAsync(string conversationId)
    {
        // The file is named by the one rule that keeps a client-supplied id inside the storage folder.
        SqliteConnection sqliteConnection = new SqliteConnection($"Data Source={GetDatabasePath(conversationId)}");

        try
        {
            // Opening the connection creates the file when the conversation has none yet.
            await sqliteConnection.OpenAsync();

            // Every caller receives a database already at the current version; a current one pays one pragma read.
            await EnsureDatabaseInitializedAsync(sqliteConnection);
        }
        catch
        {
            // A connection that failed to open or migrate is of no use to the caller, who never receives it to dispose.
            await sqliteConnection.DisposeAsync();
            throw;
        }

        // The caller owns the open connection from here.
        return sqliteConnection;
    }

    /// <inheritdoc/>
    public async Task SaveAgentConversationAsync(
        string agentIdentifier,
        AIAgent agent,
        AgentSession agentSession,
        bool isCompleted,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        // The agent framework's own options read a session back as it was written.
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        try
        {
            // The agent and the conversation whose database receives the row.
            string[] agentIdentifierParts = SplitAgentIdentifier(agentIdentifier);
            string agentName = agentIdentifierParts[0];
            string conversationId = agentIdentifierParts[1];

            // Serialized by the agent itself, not by this layer: the session's shape belongs to the
            // agent framework and only it knows how to write one that can be read back.
            JsonElement agentSessionJsonElement = await agent.SerializeSessionAsync(agentSession, jsonSerializerOptions);
            string agentSessionJsonString = JsonSerializer.Serialize(agentSessionJsonElement, jsonSerializerOptions);

            // The history the agent holds in memory, read the way a row is read back.
            IReadOnlyList<ChatMessage> agentMessages = ReadRowMessages(agentSessionJsonString, agentName, conversationId, jsonSerializerOptions);

            // One database per conversation, so every agent of it writes to the same file and nothing
            // here has to filter by conversation.
            await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);

            // Read and write are one step: a command may be rewriting this row while the agent's turn runs.
            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                // The row as it stands, to learn whether a rewrite landed behind the agent.
                await using SqliteCommand readCommand = sqliteConnection.CreateCommand();
                readCommand.Transaction = sqliteTransaction;
                readCommand.CommandText = "SELECT agent_session, is_dirty, agent_message_count FROM morgana WHERE agent_identifier = @agent_identifier;";
                readCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);

                // Ordinarily the agent's session is written as it stands and the row is clean.
                string rowToWrite = agentSessionJsonString;
                bool staysDirty = false;
                await using (SqliteDataReader rowReader = await readCommand.ExecuteReaderAsync())
                {
                    // A dirty row was rewritten behind the agent since it last read it.
                    if (await rowReader.ReadAsync() && rowReader.GetInt64(1) == 1)
                    {
                        // A row rewritten behind the agent holds a history its copy in memory never saw. Everything
                        // the agent had recorded is already in it, folded or verbatim, so only what the agent said
                        // since is appended; its own context state is written as it stands. The row stays dirty,
                        // since the agent's memory is still the history the rewrite replaced.
                        if (!rowReader.IsDBNull(2) && rowReader.GetInt32(2) is int recordedCount && recordedCount <= agentMessages.Count)
                        {
                            IReadOnlyList<ChatMessage> rewrittenMessages = ReadRowMessages(Decrypt((byte[])rowReader[0]), agentName, conversationId, jsonSerializerOptions);
                            rowToWrite = ReplaceRowMessages(
                                agentSessionJsonString, [.. rewrittenMessages, .. agentMessages.Skip(recordedCount)], agentName, conversationId, jsonSerializerOptions);
                            staysDirty = true;

                            logger.LogInformation(
                                "Kept the rewrite of {AgentIdentifier}, appending the {Count} message(s) its agent wrote since",
                                agentIdentifier, agentMessages.Count - recordedCount);
                        }
                        // Without knowing which of its messages the row already holds, the agent's own history
                        // is the one that loses nothing it said.
                        else
                        {
                            logger.LogWarning(
                                "The rewrite of {AgentIdentifier} cannot be matched to what its agent holds; the agent's history replaces it",
                                agentIdentifier);
                        }
                    }
                }

                // A session holds the whole conversation in clear: user text, tool arguments, results.
                // It is a BLOB on disk for that reason and the key never leaves configuration.
                byte[] encryptedAgentSessionJsonString = Encrypt(rowToWrite);

                // One row per agent: created on its first save, updated on every later one.
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
                string utcNow = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);

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

                // The row and its flags become visible together.
                await sqliteCommand.ExecuteNonQueryAsync();
                await sqliteTransaction.CommitAsync();

                logger.LogInformation("Saved conversation {AgentIdentifier} to database ({Length} bytes encrypted)", agentIdentifier, encryptedAgentSessionJsonString.Length);
            }
            catch
            {
                // Nothing half-written: the row keeps what it held before the save.
                await sqliteTransaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            // The agent's turn fails with its save, since a turn not kept would be lost on the next restart.
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
        // The agent framework's own options read a session back as it was written.
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        try
        {
            // The conversation whose database holds the row.
            string[] agentIdentifierParts = SplitAgentIdentifier(agentIdentifier);
            string conversationId = agentIdentifierParts[1];

            // A conversation never saved has no database: its agent starts from an empty session and nothing is created here.
            if (!await ConversationExistsAsync(conversationId))
            {
                logger.LogInformation("Conversation SQLite database for {AgentIdentifier} not found, returning null", agentIdentifier);
                return null;
            }

            // The database is opened only after it is known to exist, so this lookup never creates one.
            await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);

            // Reading the row is what brings the agent back in line with it, so the flag is cleared by the same
            // statement: no rewrite can land between the read and the clearing and go unnoticed.
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "UPDATE morgana SET is_dirty = 0 WHERE agent_identifier = @agent_identifier RETURNING agent_session;";
            sqliteCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);

            byte[] agentSessionEncryptedJsonString;
            await using (SqliteDataReader sqliteDataReader = await sqliteCommand.ExecuteReaderAsync())
            {
                // No row is an agent activated here for the first time, which starts from an empty session.
                if (!await sqliteDataReader.ReadAsync())
                {
                    logger.LogInformation("Agent session {AgentIdentifier} not found in SQLite database, returning null", agentIdentifier);
                    return null;
                }

                // The row as stored, encrypted.
                agentSessionEncryptedJsonString = (byte[])sqliteDataReader["agent_session"];
            }

            // The session in clear, as the agent framework wrote it.
            string agentSessionJsonString = Decrypt(agentSessionEncryptedJsonString);

            // The agent's memory is now exactly this row, which is what a rewrite landing before its next save
            // has to be matched against. A rewrite never touches the count, so one landing in between is still
            // measured against what was read here.
            await using SqliteCommand countCommand = sqliteConnection.CreateCommand();
            countCommand.CommandText = "UPDATE morgana SET agent_message_count = @agent_message_count WHERE agent_identifier = @agent_identifier;";
            countCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);
            countCommand.Parameters.AddWithValue("@agent_message_count",
                ReadRowMessages(agentSessionJsonString, agentIdentifierParts[0], conversationId, jsonSerializerOptions).Count);
            await countCommand.ExecuteNonQueryAsync();

            // The agent rebuilds its own session, since only the agent framework knows its shape.
            JsonElement agentSessionJsonElement = JsonSerializer.Deserialize<JsonElement>(agentSessionJsonString, jsonSerializerOptions);
            AgentSession agentSession = await agent.DeserializeSessionAsync(agentSessionJsonElement, jsonSerializerOptions);

            logger.LogInformation("Loaded conversation {AgentIdentifier} from SQLite database ({Length} bytes decrypted)", agentIdentifier, agentSessionEncryptedJsonString.Length);

            // The agent resumes on the session it left.
            return agentSession;
        }
        catch (Exception ex)
        {
            // An agent that cannot read its session must not answer as if it had none.
            logger.LogError(ex, "Failed to load conversation {AgentIdentifier}", agentIdentifier);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<string?> GetMostRecentActiveAgentAsync(string conversationId)
    {
        try
        {
            // A conversation never saved has no active agent. The lookup must not create its database.
            if (!await ConversationExistsAsync(conversationId))
            {
                logger.LogInformation("SQLite database for conversation {ConversationId} not found", conversationId);
                return null;
            }

            // The active agent is read from this conversation's database, which exists by now.
            await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);

            // The agent a resumed conversation wakes up on: active means it left a turn incomplete and
            // the most recently updated of those is the one the user was talking to.
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "SELECT agent_name FROM morgana WHERE is_active = 1 ORDER BY last_update DESC LIMIT 1;";
            string? agentName = (await sqliteCommand.ExecuteScalarAsync())?.ToString();

            logger.LogInformation(
                agentName != null
                    ? $"Most recent agent for conversation {conversationId}: {agentName}"
                    : $"No agents found for conversation {conversationId}");

            // Null leaves Morgana holding the conversation.
            return agentName;
        }
        catch (Exception ex)
        {
            // A record that cannot be read leaves Morgana holding the conversation rather than failing the turn.
            logger.LogError(ex, "Failed to get most recent agent for conversation {ConversationId}", conversationId);
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task DeactivateAgentAsync(string conversationId, string agentName)
    {
        try
        {
            // A conversation never saved has no row to switch off. The call must not create its database.
            if (!await ConversationExistsAsync(conversationId))
            {
                logger.LogInformation("SQLite database for conversation {ConversationId} not found", conversationId);
                return;
            }

            // The row is switched off in this conversation's database, which exists by now.
            await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);

            // Only is_active changes: the session and the messages are the agent's own and stay as it wrote them.
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "UPDATE morgana SET is_active = 0 WHERE agent_name = @agent_name COLLATE NOCASE AND is_active = 1;";
            sqliteCommand.Parameters.AddWithValue("@agent_name", agentName);

            // The count tells whether the row was still on active duty.
            int changedRows = await sqliteCommand.ExecuteNonQueryAsync();

            logger.LogInformation("Deactivated {Count} row(s) of agent {AgentName} for conversation {ConversationId}", changedRows, agentName, conversationId);
        }
        catch (Exception ex)
        {
            // The turn this follows has been answered already: a failed switch-off is recorded and never surfaces to the user.
            logger.LogError(ex, "Failed to deactivate agent {AgentName} for conversation {ConversationId}", agentName, conversationId);
        }
    }

    /// <inheritdoc/>
    public async Task<MorganaChatMessage[]> GetConversationHistoryAsync(
        string conversationId,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        // The agent framework's own options read every row as it was written.
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        try
        {
            // A conversation never saved has no transcript to redraw.
            if (!await ConversationExistsAsync(conversationId))
            {
                logger.LogInformation("SQLite database for conversation {ConversationId} not found, returning empty history", conversationId);
                return [];
            }

            // The database is opened only once it is known to exist, so a history read never creates one.
            await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);

            // Every participant's row, in the order the participants first spoke.
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "SELECT agent_name, agent_session, is_active FROM morgana ORDER BY creation_date ASC;";
            await using SqliteDataReader sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();

            // Every message of every participant, with who wrote it and whether that participant has finished.
            List<(string agentName, bool agentCompleted, ChatMessage message)> allMessages = [];

            while (await sqliteDataReader.ReadAsync())
            {
                // A row no longer active belongs to a participant that handed the conversation back.
                string agentName = (string)sqliteDataReader["agent_name"];
                bool agentCompleted = (long)sqliteDataReader["is_active"] == 0;

                // Every participant's row is read the same way, the orchestrator's included: what
                // differs between them is what else the row carries, never where the transcript sits.
                byte[] encryptedAgentSessionJsonString = (byte[])sqliteDataReader["agent_session"];
                IReadOnlyList<ChatMessage> chatMessages = ReadRowMessages(
                    Decrypt(encryptedAgentSessionJsonString), agentName, conversationId, jsonSerializerOptions);

                // Every message joins the list, the intermediate ones included: they are filtered only after
                // their buttons and cards have been extracted, which then attach to the turn's final answer.
                allMessages.AddRange(
                    chatMessages.Select(message => (agentName, agentCompleted, message)));
            }

            // The rows come in the order the participants first spoke, so the messages are put back in time order;
            // tool results are no part of what a user reads.
            return ProcessMessagesForHistory(
                conversationId,
                allMessages: [
                    .. allMessages.Where(entry => entry.message.Role != ChatRole.Tool)
                                  .OrderBy(entry => entry.message.CreatedAt?.UtcDateTime ?? DateTime.UtcNow)
                ]);
        }
        catch (Exception ex)
        {
            // A transcript that cannot be read is an error the channel shows, never an empty conversation.
            logger.LogError(ex, "Failed to retrieve conversation history for {ConversationId}", conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task AppendOrchestratorMessagesAsync(
        string conversationId,
        IReadOnlyList<ChatMessage> messages)
    {
        // Nothing to file.
        if (messages.Count == 0)
            return;

        // Morgana's row is addressed like an agent's, under her own name.
        string orchestratorIdentifier = $"{Constants.Morgana}-{conversationId}";

        try
        {
            // The orchestrator speaks before any agent does, since the welcome opens the conversation,
            // so this may be the first write the database ever receives.
            await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);

            // Read and write are one step: the user's phrase arrives on one path while an answer
            // leaves on another. Either losing the other's line would tear a hole in the dialogue.
            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                // Morgana's messages so far, none before her first word.
                await using SqliteCommand readCommand = sqliteConnection.CreateCommand();
                readCommand.Transaction = sqliteTransaction;
                readCommand.CommandText = "SELECT agent_session FROM morgana WHERE agent_identifier = @agent_identifier;";
                readCommand.Parameters.AddWithValue("@agent_identifier", orchestratorIdentifier);
                object? storedRow = await readCommand.ExecuteScalarAsync();
                List<ChatMessage> storedMessages = storedRow is byte[] encryptedStoredRow
                    ? [.. ReadRowMessages(Decrypt(encryptedStoredRow), Constants.Morgana, conversationId)]
                    : [];

                // The new messages go after them, in the row's one shape.
                storedMessages.AddRange(messages);
                byte[] encryptedRow = Encrypt(WriteRowMessages(storedMessages));

                // Her row is never active: Morgana holds the conversation without being an agent that keeps it.
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
                writeCommand.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture));

                // The row becomes visible whole.
                await writeCommand.ExecuteNonQueryAsync();
                await sqliteTransaction.CommitAsync();

                logger.LogInformation(
                    "Appended {Count} orchestrator message(s) to conversation {ConversationId} — {Total} on record",
                    messages.Count, conversationId, storedMessages.Count);
            }
            catch
            {
                // Nothing half-written: the row keeps what it held.
                await sqliteTransaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            // A line Morgana cannot keep fails its caller, which decides what the user is told.
            logger.LogError(ex, "Failed to append orchestrator messages to conversation {ConversationId}", conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChatMessage>> LoadParticipantMessagesAsync(string conversationId, string agentName)
    {
        // A conversation nobody has spoken in has no database, which is not a failure to report.
        if (!await ConversationExistsAsync(conversationId))
            return [];

        // The participant's row lives in the conversation's own database.
        await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);

        // An agent is addressed by the identifier its own turns write under, so the caller names the agent
        // and the conversation rather than having to know how the two are spelled together.
        await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "SELECT agent_session FROM morgana WHERE agent_identifier = @agent_identifier;";
        sqliteCommand.Parameters.AddWithValue("@agent_identifier", $"{agentName}-{conversationId}");

        // An agent that has never taken a turn here holds no history, which the caller reads as nothing to do.
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
        // The same identifier the agent's own turns write under: a row is reached by who wrote it and where.
        string agentIdentifier = $"{agentName}-{conversationId}";

        try
        {
            // The participant's row is rewritten in the conversation's own database.
            await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);

            // Read and write are one step: the agent this row belongs to may be writing its own turn.
            await using SqliteTransaction sqliteTransaction = sqliteConnection.BeginTransaction();
            try
            {
                // The row as it stands now.
                await using SqliteCommand readCommand = sqliteConnection.CreateCommand();
                readCommand.Transaction = sqliteTransaction;
                readCommand.CommandText = "SELECT agent_session FROM morgana WHERE agent_identifier = @agent_identifier;";
                readCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);

                // An agent with no row has no session to correct: creating one here would invent a
                // participant the conversation never had.
                if (await readCommand.ExecuteScalarAsync() is not byte[] encryptedRow)
                    throw new InvalidOperationException($"No row for '{agentName}' in conversation {conversationId}.");

                // What the row holds now decides whether the caller still describes it: an agent only ever
                // appends to its own history, so a row that grew shorter is one this caller read in another life.
                string storedRow = Decrypt(encryptedRow);
                IReadOnlyList<ChatMessage> storedMessages = ReadRowMessages(storedRow, agentName, conversationId);
                if (storedMessages.Count < messagesReadCount)
                {
                    // The row is left as it is and the caller is told its rewrite did not land.
                    await sqliteTransaction.RollbackAsync();
                    logger.LogWarning(
                        "The row of '{AgentName}' in conversation {ConversationId} holds {StoredCount} message(s) where the caller read {ReadCount}; leaving it alone",
                        agentName, conversationId, storedMessages.Count, messagesReadCount);
                    return false;
                }

                // A turn the agent saved while the caller was working is kept exactly as the agent wrote it:
                // the caller speaks for the messages it read, never for what was said after them.
                List<ChatMessage> rewrittenMessages = [.. messages, .. storedMessages.Skip(messagesReadCount)];

                // Only the history is swapped: an agent's row also carries the context variables its session
                // holds, which belong to the agent and to nobody correcting its transcript.
                string rewrittenRow = ReplaceRowMessages(storedRow, rewrittenMessages, agentName, conversationId);

                await using SqliteCommand writeCommand = sqliteConnection.CreateCommand();
                writeCommand.Transaction = sqliteTransaction;

                // The row keeps the state it had: whether the agent is still working on the conversation
                // says nothing about its history having been rewritten. It turns dirty with the rewrite, since
                // the agent's copy in memory still holds the history this write replaced.
                writeCommand.CommandText =
                    "UPDATE morgana SET agent_session = @agent_session, last_update = @now, is_dirty = 1 WHERE agent_identifier = @agent_identifier;";
                writeCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);
                writeCommand.Parameters.AddWithValue("@agent_session", Encrypt(rewrittenRow));
                writeCommand.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture));

                // The rewrite becomes visible whole.
                await writeCommand.ExecuteNonQueryAsync();
                await sqliteTransaction.CommitAsync();

                logger.LogInformation(
                    "Rewrote the {Count} message(s) of '{AgentName}' in conversation {ConversationId}, keeping {KeptCount} written since",
                    messages.Count, agentName, conversationId, storedMessages.Count - messagesReadCount);

                // The rewrite landed.
                return true;
            }
            catch
            {
                // Nothing half-written: the row keeps what it held.
                await sqliteTransaction.RollbackAsync();
                throw;
            }
        }
        catch (Exception ex)
        {
            // The command reports the failure to the user who asked for it.
            logger.LogError(ex, "Failed to rewrite the messages of '{AgentName}' in conversation {ConversationId}", agentName, conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task SaveChannelMetadataAsync(string conversationId, ChannelMetadata metadata)
    {
        try
        {
            // The handshake goes to the conversation's own database, created on first write: it may
            // precede any agent execution, so the schema has to exist before the upsert.
            await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);

            // The conversation has one handshake, which a repeated start overwrites.
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
            // A channel with no callback and no length cap stores nulls, never empty values.
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
            // A conversation whose handshake is not kept cannot be served after a restart, so its start fails.
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
            if (!await ConversationExistsAsync(conversationId))
            {
                logger.LogInformation("SQLite database for conversation {ConversationId} not found, no persisted channel metadata", conversationId);
                return null;
            }

            // The handshake is the conversation's single channel row, read from its own database.
            await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);

            // The single channel row of the conversation.
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText =
"""
SELECT channel_name, delivery_mode, callback_url, supports_rich_cards, supports_quick_replies, supports_streaming, supports_markdown, max_message_length
FROM channel_metadata
WHERE id = 1;
""";

            // A database without the row belongs to a conversation never started.
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

            // The handshake as the store hands it to every reader.
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
        catch (Exception ex)
        {
            // A handshake that cannot be read must not pass for a conversation that has none.
            logger.LogError(ex, "Failed to load channel metadata for conversation {ConversationId}", conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task UpsertSharedVariableAsync(string conversationId, string variableName, object variableValue, string sourceAgentIntent)
    {
        try
        {
            // The variable is written to the conversation's own database: it can be written before the agent
            // has saved its first session, so the schema has to exist before the upsert.
            await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);

            // The value as JSON, encrypted like the rows: a shared variable is a datum of the customer's, such as a customer code.
            string serializedValue = JsonSerializer.Serialize(variableValue);
            byte[] encryptedValue = Encrypt(serializedValue);

            // The first agent to write a variable owns it for the conversation's life and every later write is ignored,
            // as MorganaAIContextProvider merges the registry on the read side.
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText =
                """
                INSERT OR IGNORE INTO shared_context
                    (variable_name, variable_value, source_agent_intent, last_update)
                VALUES (@variable_name, @variable_value, @source_agent_intent, @last_update);
                """;
            sqliteCommand.Parameters.AddWithValue("@variable_name", variableName);
            sqliteCommand.Parameters.AddWithValue("@variable_value", encryptedValue);
            sqliteCommand.Parameters.AddWithValue("@source_agent_intent", sourceAgentIntent);
            sqliteCommand.Parameters.AddWithValue("@last_update", DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture));

            // A row written means this agent was the first writer.
            int rowsAffected = await sqliteCommand.ExecuteNonQueryAsync();

            logger.LogInformation(
                rowsAffected > 0
                    ? "Shared variable '{VariableName}' persisted by '{Source}' for conversation {ConversationId} (first writer)"
                    : "Shared variable '{VariableName}' from '{Source}' for conversation {ConversationId} ignored (already claimed by an earlier writer)",
                variableName, sourceAgentIntent, conversationId);
        }
        catch (Exception ex)
        {
            // The tool that wrote the variable reports the failure to the model.
            logger.LogError(ex, "Failed to upsert shared variable '{VariableName}' for conversation {ConversationId}", variableName, conversationId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<Dictionary<string, object>> LoadSharedVariablesAsync(string conversationId)
    {
        try
        {
            // A conversation with no database has no shared variable yet.
            if (!await ConversationExistsAsync(conversationId))
                return [];

            // The registry lives in the conversation's own database, which exists by now.
            await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(conversationId);

            // Every variable of the conversation, whichever agent wrote it.
            await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
            sqliteCommand.CommandText = "SELECT variable_name, variable_value FROM shared_context;";

            Dictionary<string, object> sharedVariables = new();
            await using SqliteDataReader sqliteDataReader = await sqliteCommand.ExecuteReaderAsync();
            while (await sqliteDataReader.ReadAsync())
            {
                // The name in clear and the value as stored, encrypted JSON.
                string variableName = (string)sqliteDataReader["variable_name"];
                byte[] encryptedValue = (byte[])sqliteDataReader["variable_value"];
                JsonElement element = JsonSerializer.Deserialize<JsonElement>(Decrypt(encryptedValue));

                // Convert back to a "natural" .NET value so callers (and the LLM via
                // GetContextVariable) see the same shape that was originally written. Without
                // this unwrap, primitives would round-trip as JsonElement and the framework's
                // tool-result serialiser would re-encode them with extra JSON wrapping.
                object? value = element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.TryGetInt64(out long integerValue) ? (object)integerValue : element.GetDouble(),
                    JsonValueKind.True   => true,
                    JsonValueKind.False  => false,
                    JsonValueKind.Null   => null,
                    _                    => element
                };

                // A variable written as null holds nothing to read back.
                if (value is not null)
                    sharedVariables[variableName] = value;
            }

            // The registry every agent of the conversation merges at its turn's start.
            return sharedVariables;
        }
        catch (Exception ex)
        {
            // A registry that cannot be read must not pass for an empty one.
            logger.LogError(ex, "Failed to load shared variables for conversation {ConversationId}", conversationId);
            throw;
        }
    }

    #region Utilities

    /// <inheritdoc />
    public async Task<bool> IsDirtyAsync(string agentIdentifier)
    {
        // The conversation's database is named by the identifier's second part.
        string[] agentIdentifierParts = SplitAgentIdentifier(agentIdentifier);

        // An agent whose conversation has no database has no row that anybody could have rewritten.
        if (!await ConversationExistsAsync(agentIdentifierParts[1]))
            return false;

        // The conversation's database exists by the check above.
        await using SqliteConnection sqliteConnection = await OpenDatabaseAsync(agentIdentifierParts[1]);

        // The dirty flag of the agent's own row.
        await using SqliteCommand sqliteCommand = sqliteConnection.CreateCommand();
        sqliteCommand.CommandText = "SELECT is_dirty FROM morgana WHERE agent_identifier = @agent_identifier;";
        sqliteCommand.Parameters.AddWithValue("@agent_identifier", agentIdentifier);

        // No row yet is an agent's first activation, which reads its record anyway.
        return await sqliteCommand.ExecuteScalarAsync() is long and 1;
    }

    /// <inheritdoc/>
    public Task<bool> ConversationExistsAsync(string conversationId)
        // On SQLite the conversation is its database file, which a lookup on the local disk answers.
        => Task.FromResult(File.Exists(GetDatabasePath(conversationId)));

    /// <summary>Splits an agent identifier into the agent's name and its conversation id, refusing any other shape.</summary>
    private static string[] SplitAgentIdentifier(string agentIdentifier)
    {
        // Split at the first dash only: a conversation id is a GUID and carries dashes of its own.
        string[] agentIdentifierParts = agentIdentifier.Split('-', 2);

        // An identifier with no dash names no conversation.
        if (agentIdentifierParts.Length != 2)
            throw new ArgumentException($"Invalid agent_identifier format: '{agentIdentifier}'. Expected format: '{{agent_name}}-{{conversation_id}}'");

        // The agent's name first, the conversation id second.
        return agentIdentifierParts;
    }

    /// <summary>Returns the path of a conversation's database, <c>morgana-{conversationId}.sqlite</c> in the storage folder.</summary>
    private string GetDatabasePath(string conversationId)
    {
        // The id comes from the client, so every character a file name cannot hold is replaced: no id climbs out of the folder.
        string sanitizedConversationId = string.Join("_", conversationId.Split(Path.GetInvalidFileNameChars()));

        // One file per conversation, side by side in the storage folder.
        return Path.Combine(options.StoragePath, $"morgana-{sanitizedConversationId}.sqlite");
    }

    /// <summary>Creates the schema of a database opened for the first time, on a connection already open.</summary>
    private async Task EnsureDatabaseInitializedAsync(SqliteConnection connection)
    {
        // The schema version a database already carries, zero for a new file.
        await using SqliteCommand checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "PRAGMA user_version;";
        long currentVersion = (long)(await checkCommand.ExecuteScalarAsync() ?? 0L);

        // A database at the current version needs nothing more.
        if (currentVersion >= 6)
            return;

        // Every table of the current schema, the budget row seeded so a charge only has to raise it.
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

        // The database is marked with the current version, so the next open skips the schema.
        await using SqliteCommand versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version = 6;";
        await versionCommand.ExecuteNonQueryAsync();

        logger.LogInformation(
            "Initialized database schema v6 for: {GetFileName}", Path.GetFileName(connection.DataSource));
    }

    /// <summary>Encrypts a text with AES-256-CBC, returning the IV followed by the ciphertext.</summary>
    private byte[] Encrypt(string plaintext)
    {
        // The configured key, shared by every row of every conversation.
        using Aes aes = Aes.Create();
        aes.Key = encryptionKey;
        // A fresh random IV every single call, never reused across rows or across saves of the
        // same row: CBC mode leaks patterns between ciphertexts that share both key and IV, so
        // reuse would be the actual vulnerability, not just a decrypt-time inconvenience.
        aes.GenerateIV();

        // The ciphertext is written into one buffer.
        using ICryptoTransform encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using MemoryStream msEncrypt = new MemoryStream();

        // The IV isn't secret, only unique: it travels in clear in front of the ciphertext, where Decrypt reads it.
        msEncrypt.Write(aes.IV, 0, aes.IV.Length);

        // The stream completes the last block when it closes, so the buffer is read only after it.
        using (CryptoStream csEncrypt = new CryptoStream(msEncrypt, encryptor, CryptoStreamMode.Write))
        using (StreamWriter swEncrypt = new StreamWriter(csEncrypt))
        {
            swEncrypt.Write(plaintext);
        }

        // The IV and the ciphertext, as one blob.
        return msEncrypt.ToArray();
    }

    /// <summary>Decrypts a blob that <see cref="Encrypt"/> wrote, the IV in front of the ciphertext.</summary>
    private string Decrypt(byte[] ciphertext)
    {
        // The configured key, shared by every row of every conversation.
        using Aes aes = Aes.Create();
        aes.Key = encryptionKey;

        // The IV that Encrypt put in front of the ciphertext.
        byte[] iv = new byte[aes.IV.Length];
        Array.Copy(ciphertext, 0, iv, 0, iv.Length);
        aes.IV = iv;

        // The ciphertext is what follows the IV.
        using ICryptoTransform decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using MemoryStream msDecrypt = new MemoryStream(ciphertext, iv.Length, ciphertext.Length - iv.Length);
        using CryptoStream csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
        using StreamReader srDecrypt = new StreamReader(csDecrypt);

        // The text as it was encrypted.
        return srDecrypt.ReadToEnd();
    }

    /// <summary>
    /// Reads the messages a row holds, empty for a row whose shape carries none, so one unreadable participant costs only its own lines.
    /// The one place that knows how a row encodes its messages, with <see cref="WriteRowMessages"/>: an agent's row is its session,
    /// Morgana's the same outer shape with messages alone inside.
    /// </summary>
    private IReadOnlyList<ChatMessage> ReadRowMessages(
        string rowJson,
        string authorName,
        string conversationId,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        // The agent framework's own options read the row as it was written.
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;
        JsonElement rowElement = JsonSerializer.Deserialize<JsonElement>(rowJson, jsonSerializerOptions);

        // The transcript sits three levels down: the state bag, the history provider's slot, its messages.
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

        // The messages in the order they were said.
        return JsonSerializer.Deserialize<ChatMessage[]>(messagesElement.GetRawText(), jsonSerializerOptions)
               ?? throw new InvalidOperationException(
                   $"Failed to deserialize messages for '{authorName}' in conversation {conversationId}");
    }

    /// <summary>
    /// Builds the row of a participant that holds messages and nothing else, which is Morgana alone.
    /// It is read back exactly as an agent's row is, so a transcript is assembled without asking who wrote which row.
    /// </summary>
    private static string WriteRowMessages(
        IReadOnlyList<ChatMessage> messages,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        // The agent framework's own options, as an agent's row is written with.
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        // The same three levels an agent's session carries, with the messages alone at the bottom.
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
    /// Returns a row with its history replaced by the given messages and everything else untouched.
    /// An agent's row carries its context state beside its history, so the history is swapped where it sits.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the row does not hold a history to replace.</exception>
    private static string ReplaceRowMessages(
        string rowJson,
        IReadOnlyList<ChatMessage> messages,
        string authorName,
        string conversationId,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        // The agent framework's own options, as the row was written with.
        jsonSerializerOptions ??= AgentAbstractionsJsonUtilities.DefaultOptions;

        // The row travels as the session wrote it, so it is taken apart rather than modelled: what an agent
        // keeps beside its history is its own business and must come out the other side untouched.
        JsonNode rowNode = JsonNode.Parse(rowJson)
            ?? throw new InvalidOperationException($"The row of '{authorName}' in conversation {conversationId} is empty.");

        // A row without the history the chat provider keeps is one this rewrite cannot be about: writing a
        // history into it would invent state the agent never had.
        if (rowNode[RowStateBagProperty]?[RowHistoryStateKey] is not JsonObject historyState)
            throw new InvalidOperationException(
                $"The row of '{authorName}' in conversation {conversationId} holds no history to rewrite.");

        // The messages take the place of the ones that were there, where they were, so the agent reads them
        // back as its own history and finds everything else exactly as it left it.
        historyState[RowMessagesProperty] = JsonNode.Parse(JsonSerializer.Serialize(messages, jsonSerializerOptions));

        // The row with its new history.
        return rowNode.ToJsonString(jsonSerializerOptions);
    }

    /// <summary>Turns the participants' messages into the transcript a channel redraws: one bubble per turn, with the buttons and card it delivered.</summary>
    private MorganaChatMessage[] ProcessMessagesForHistory(
        string conversationId,
        List<(string agentName, bool agentCompleted, ChatMessage message)> allMessages)
    {
        // The transcript as it is built.
        List<MorganaChatMessage> historyMessages = [];

        // The participants whose history marks the answer of each turn: their intermediate messages, the tool loop's
        // scratchpad, stay out of the transcript. Morgana's own row carries no marker, so every message of hers is shown.
        // A message the reducer stamped with its summary is a real turn and is shown like any other.
        HashSet<string> markedAgents =
        [
            .. allMessages
                .Where(entry => entry.message.Role == ChatRole.Assistant
                                && entry.message.AdditionalProperties?.ContainsKey(Constants.MessageProperties.UserFacing) == true)
                .Select(entry => entry.agentName)
        ];

        foreach ((string agentName, bool agentCompleted, ChatMessage chatMessage) in allMessages)
        {
            // The agent holds this phrase only so its model could read it: no agent was active when it arrived,
            // so Morgana saved it on her own side and the user reads it once, from there.
            if (chatMessage.Role == ChatRole.User
                 && chatMessage.AdditionalProperties?.ContainsKey(Constants.MessageProperties.ContextOnly) == true)
                continue;

            // An agent's turn is shown through the one answer MorganaAgent marked at its end: the messages of the
            // tool loop before it are its scratchpad.
            if (chatMessage.Role == ChatRole.Assistant
                 && markedAgents.Contains(agentName)
                 && chatMessage.AdditionalProperties?.ContainsKey(Constants.MessageProperties.UserFacing) != true)
                continue;

            // A message with no text has nothing for a bubble.
            string messageText = ExtractTextFromMessage(chatMessage);
            if (string.IsNullOrWhiteSpace(messageText))
                continue;

            // The buttons the turn delivered, recorded beside its answer; a message that recorded none showed none.
            string? recordedQuickReplies = chatMessage.ReadTextProperty(Constants.MessageProperties.TurnQuickReplies);
            List<QuickReply>? quickReplies = recordedQuickReplies is null
                ? null
                : JsonSerializer.Deserialize<List<QuickReply>>(recordedQuickReplies, Records.DefaultJsonSerializerOptions);

            // The card the turn delivered, recorded the same way.
            string? recordedRichCard = chatMessage.ReadTextProperty(Constants.MessageProperties.TurnRichCard);
            RichCard? richCard = recordedRichCard is null
                ? null
                : JsonSerializer.Deserialize<RichCard>(recordedRichCard, Records.DefaultJsonSerializerOptions);

            // The bubble, with whatever widgets its turn delivered.
            historyMessages.Add(
                MapToMorganaChatMessage(conversationId, agentName, agentCompleted, chatMessage, isLastHistoryMessage: false, quickReplies, richCard));
        }

        // Mark the last emitted message (the one actually rendered in the UI) as the last
        // history message, so trailing quick replies/rich cards on it stay interactive after
        // a resume/refresh instead of being frozen because the raw session tail was empty.
        if (historyMessages.Count > 0)
        {
            // The last bubble rendered, whatever the raw tail holds.
            int lastIndex = historyMessages.Count - 1;
            historyMessages[lastIndex] = historyMessages[lastIndex] with { IsLastHistoryMessage = true };
        }

        logger.LogInformation(
            "Processed {HistoryMessagesCount} messages (filtered from {AllMessagesCount} raw messages) for {ConversationId}", historyMessages.Count, allMessages.Count, conversationId);

        // The transcript in the order it was said.
        return [.. historyMessages];
    }

    /// <summary>
    /// Returns the text a message shows: the turn's delivered reply when it carries one, otherwise its own text joined.
    /// A turn that called a tool wrote its answer across several messages and only the last survives the filter,
    /// so the recorded <see cref="Constants.MessageProperties.TurnText"/> is what the user read; a user turn carries none.
    /// </summary>
    private string ExtractTextFromMessage(ChatMessage chatMessage)
    {
        // What the user actually read, whole, when the turn recorded it.
        string? turnText = chatMessage.ReadTextProperty(Constants.MessageProperties.TurnText);
        if (!string.IsNullOrWhiteSpace(turnText))
            return turnText;

        // Nothing a transcript could show. A message stripped of its content is not a turn.
        if (chatMessage.Contents == null || chatMessage.Contents.Count == 0)
            return string.Empty;

        // Every text fragment joined into one line: a model may split an answer across several content parts,
        // while a transcript shows one message per turn. Text alone, since an image or a data part has no shape on the wire.
        return string.Join(" ", chatMessage.Contents
            .OfType<TextContent>()
            .Where(textContent => !string.IsNullOrEmpty(textContent.Text))
            .Select(textContent => textContent.Text.Trim()));
    }

    /// <summary>Builds the bubble a channel renders for one message of the transcript.</summary>
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