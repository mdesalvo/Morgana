using System.Text.Json;
using Microsoft.Agents.AI;
using static Morgana.AI.Records;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Service for persisting and loading conversation state (AgentSession) across application restarts.
/// Enables resuming conversations from where they left off by serializing session state to durable storage.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service abstracts the persistence layer for conversation state, allowing different implementations
/// such as file-based storage, database storage, or cloud blob storage. Each conversation is uniquely identified
/// by conversationId and contains the complete state needed to resume the conversation.</para>
/// <para><strong>Persisted State:</strong></para>
/// <list type="bullet">
/// <item><term>Message History</term><description>All user and assistant messages in the conversation</description></item>
/// <item><term>Context Variables</term><description>Agent context variables (userId, invoiceId, etc.)</description></item>
/// <item><term>Shared Variable Names</term><description>Configuration of which variables are shared across agents</description></item>
/// </list>
/// <para><strong>Usage Pattern:</strong></para>
/// <code>
/// // Save conversation after each turn
/// await persistenceService.SaveAgentConversationAsync(conversationId, AgentSession);
///
/// // Load conversation on subsequent requests
/// AgentSession? restored = await persistenceService.LoadAgentConversationAsync(conversationId, morganaAgent);
/// if (restored != null)
/// {
///     // Continue conversation with restored state
///     await aiAgent.RunAsync(userMessage, restored);
/// }
/// </code>
/// <para><strong>Implementation Examples:</strong></para>
/// <list type="bullet">
/// <item><term>SqlConversationPersistenceService</term><description>Stores conversations in SQL database</description></item>
/// <item><term>CosmosDbConversationPersistenceService</term><description>Stores conversations in Azure Cosmos DB (future)</description></item>
/// </list>
/// </remarks>
public interface IConversationPersistenceService
{
    /// <summary>
    /// Saves the complete conversation state of the given agent to persistent storage.
    /// Serializes the AgentSession including message history, context variables, and metadata.
    /// </summary>
    /// <param name="agentIdentifier">Unique identifier for the agent's conversation</param>
    /// <param name="agent">AIAgent instance corresponding to the running agent</param>
    /// <param name="agentSession">AgentSession instance containing the complete conversation state</param>
    /// <param name="isCompleted">Flag indicating if the agent is signalling completion of the conversation</param>
    /// <param name="jsonSerializerOptions">JSON serialization options (optional, uses AgentAbstractionsJsonUtilities.DefaultOptions if null)</param>
    /// <returns>Task representing the async save operation</returns>
    /// <remarks>
    /// <para><strong>Thread Safety:</strong></para>
    /// <para>Implementations should handle concurrent saves to the same agentIdentifier appropriately,
    /// typically using last-write-wins semantics or file locking mechanisms.</para>
    /// <para><strong>Error Handling:</strong></para>
    /// <para>Implementations should throw meaningful exceptions for I/O errors, encryption failures,
    /// or serialization errors to allow proper error handling by callers.</para>
    /// </remarks>
    Task SaveAgentConversationAsync(
        string agentIdentifier,
        AIAgent agent,
        AgentSession agentSession,
        bool isCompleted,
        JsonSerializerOptions? jsonSerializerOptions = null);

    /// <summary>
    /// Loads a previously saved agent's conversation state from persistent storage.
    /// Deserializes the AgentSession and reconnects all AI context providers and callbacks.
    /// </summary>
    /// <param name="agentIdentifier">Unique identifier for the agent's conversation to load</param>
    /// <param name="agent">MorganaAgent instance that will receive the deserialized session</param>
    /// <param name="jsonSerializerOptions">JSON serialization options (optional, uses AgentAbstractionsJsonUtilities.DefaultOptions if null)</param>
    /// <returns>Deserialized AgentSession if conversation exists, null if not found</returns>
    /// <remarks>
    /// <para><strong>Null Return Semantics:</strong></para>
    /// <para>Returns null when the agentIdentifier has never been saved, indicating this is a new conversation.
    /// Callers should create a new AgentSession in this case via agent.GetNewSessionAsync().</para>
    /// <para><strong>Deserialization Process:</strong></para>
    /// <list type="number">
    /// <item>Read and decrypt (if applicable) the serialized session data</item>
    /// <item>Deserialize JSON to JsonElement</item>
    /// <item>Call agent.DeserializeSessionAsync() to reconstruct the full thread state</item>
    /// <item>Return the fully restored AgentSession</item>
    /// </list>
    /// </remarks>
    Task<AgentSession?> LoadAgentConversationAsync(
        string agentIdentifier,
        Abstractions.MorganaAgent agent,
        JsonSerializerOptions? jsonSerializerOptions = null);

    /// <summary>
    /// Gets the most recently active agent for a conversation.
    /// Uses last_update timestamp to determine which agent was last engaged.
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    /// <returns>Agent name (e.g., "billing") or null if conversation not found</returns>
    Task<string?> GetMostRecentActiveAgentAsync(string conversationId);

    /// <summary>
    /// Retrieves the complete conversation history across all agents for a given conversation.
    /// Decrypts, deserializes, and chronologically orders messages from all participating agents.
    /// </summary>
    /// <param name="conversationId">Conversation identifier</param>
    /// <param name="jsonSerializerOptions">JSON serialization options (optional, uses AgentAbstractionsJsonUtilities.DefaultOptions if null)</param>
    /// <returns>Array of MorganaChatMessage ordered by creation timestamp, or empty array if conversation not found</returns>
    /// <remarks>
    /// <para><strong>Process Flow:</strong></para>
    /// <list type="number">
    /// <item>Load all agent rows from SQLite database for the conversation</item>
    /// <item>For each agent: decrypt agent_session BLOB and deserialize to AgentSession JSON structure</item>
    /// <item>Extract ChatMessage array from each AgentSession.Messages</item>
    /// <item>Reconcile messages from all agents and sort by CreatedAt timestamp</item>
    /// <item>Map each Microsoft.Agents.AI.ChatMessage to MorganaChatMessage record</item>
    /// </list>
    /// <para><strong>Failure Semantics:</strong></para>
    /// <para>Fails fast on any deserialization error - no partial/incomplete history is returned.
    /// This ensures UI always displays complete, consistent conversation state.</para>
    /// </remarks>
    Task<MorganaChatMessage[]> GetConversationHistoryAsync(
        string conversationId,
        JsonSerializerOptions? jsonSerializerOptions = null);

    /// <summary>
    /// Ensures the conversation database exists and is initialized with the latest schema.
    /// Idempotent - safe to call multiple times (checks PRAGMA user_version).
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation</param>
    /// <returns>Task representing the async initialization operation</returns>
    /// <remarks>
    /// <para><strong>Use Cases:</strong></para>
    /// <list type="bullet">
    /// <item>Called by rate limiter before first message (if no agent executed yet)</item>
    /// <item>Called by agent persistence before saving session</item>
    /// <item>Ensures database exists even if user sends message before agent activation</item>
    /// </list>
    /// <para><strong>Schema Version Management:</strong></para>
    /// <para>This method checks PRAGMA user_version and creates/migrates schema as needed.
    /// Current version: 3 (adds channel_capabilities table on top of v2's rate_limit_log).</para>
    /// </remarks>
    Task EnsureDatabaseInitializedAsync(string conversationId);

    /// <summary>
    /// Persists the channel capabilities declared by the client at conversation start.
    /// Stored as a single row in the <c>channel_capabilities</c> table (id = 1) of the
    /// per-conversation database. If no row exists this method writes one; otherwise it
    /// replaces the existing row (clients are not expected to handshake more than once,
    /// but the upsert keeps the operation idempotent).
    /// </summary>
    /// <param name="conversationId">Conversation identifier (used to locate the per-conversation DB).</param>
    /// <param name="capabilities">Capability set advertised by the originating channel.</param>
    /// <remarks>
    /// <para><strong>First-writer pattern:</strong></para>
    /// <para>This method may be the very first persistence call for a brand-new conversation
    /// (the capability handshake happens before any agent has executed). The implementation
    /// MUST therefore call <see cref="EnsureDatabaseInitializedAsync(string)"/> internally so
    /// that the database file and schema exist before the INSERT.</para>
    /// </remarks>
    Task SaveChannelCapabilitiesAsync(string conversationId, Records.ChannelCapabilities capabilities);

    /// <summary>
    /// Loads the channel capabilities previously persisted for a conversation. Returns
    /// <c>null</c> when the conversation database does not exist or contains no capability
    /// row (e.g. legacy conversations created before the capability handshake was introduced),
    /// in which case callers should fall back to the channel's hard-coded full capabilities.
    /// </summary>
    /// <param name="conversationId">Conversation identifier (used to locate the per-conversation DB).</param>
    /// <returns>The persisted <see cref="Records.ChannelCapabilities"/>, or null if absent.</returns>
    Task<Records.ChannelCapabilities?> LoadChannelCapabilitiesAsync(string conversationId);

    /// <summary>
    /// Reports whether the given conversation is known to the underlying store. Used by the
    /// restore path to distinguish a genuine existing conversation (possibly on an older
    /// schema) from a stale client-side identifier pointing to a conversation that was never
    /// materialised — so the caller can reject the restore instead of fabricating empty state
    /// on its behalf. Implementations decide what "exists" means in their backend: the SQLite
    /// implementation checks for the per-conversation database file, a SQL Server or
    /// PostgreSQL implementation would probe a conversations table, a blob-store
    /// implementation would probe the corresponding object.
    /// </summary>
    /// <param name="conversationId">Conversation identifier.</param>
    /// <returns><c>true</c> if the conversation is present in the store, <c>false</c> otherwise.</returns>
    bool ConversationExists(string conversationId);
}