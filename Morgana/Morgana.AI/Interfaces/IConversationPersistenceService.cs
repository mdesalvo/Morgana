using System.Text.Json;
using Microsoft.Agents.AI;
using Morgana.AI.Providers;
using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Persistence abstraction for AgentSession state: SaveAgentConversationAsync saves session (message history, context variables);
/// LoadAgentConversationAsync loads and restores a previously saved session (null if not found, indicating new conversation).
/// SaveAgentConversationAsync handles serialization/encryption;
/// LoadAgentConversationAsync handles deserialization/decryption plus reconnection of AI context providers.
/// </summary>
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
    /// Current version: 3 (adds channel_metadata table on top of v2's rate_limit_log).</para>
    /// </remarks>
    Task EnsureDatabaseInitializedAsync(string conversationId);

    /// <summary>
    /// Persists the channel metadata (channel name + capability budget) declared by the client
    /// at conversation start. Stored as a single row in the <c>channel_metadata</c> table
    /// (id = 1) of the per-conversation database. If no row exists this method writes one;
    /// otherwise it replaces the existing row (clients are not expected to handshake more than
    /// once, but the upsert keeps the operation idempotent).
    /// </summary>
    /// <param name="conversationId">Conversation identifier (used to locate the per-conversation DB).</param>
    /// <param name="metadata">Metadata advertised by the originating channel.</param>
    /// <remarks>
    /// <para><strong>First-writer pattern:</strong></para>
    /// <para>This method may be the very first persistence call for a brand-new conversation
    /// (the channel handshake happens before any agent has executed). The implementation
    /// MUST therefore call <see cref="EnsureDatabaseInitializedAsync(string)"/> internally so
    /// that the database file and schema exist before the INSERT.</para>
    /// </remarks>
    Task SaveChannelMetadataAsync(string conversationId, ChannelMetadata metadata);

    /// <summary>
    /// Loads the channel metadata previously persisted for a conversation. Returns
    /// <c>null</c> when the conversation database does not exist or contains no metadata
    /// row (e.g. legacy conversations created before the channel handshake was introduced),
    /// in which case callers should fall back to the channel's hard-coded default metadata.
    /// </summary>
    /// <param name="conversationId">Conversation identifier (used to locate the per-conversation DB).</param>
    /// <returns>The persisted <see cref="ChannelMetadata"/>, or null if absent.</returns>
    Task<ChannelMetadata?> LoadChannelMetadataAsync(string conversationId);

    /// <summary>
    /// Persists shared context variable into conversation-scoped shared_context registry for cross-agent access.
    /// First-write-wins: implementations MUST ignore subsequent upserts with different values (SQLite: INSERT OR IGNORE).
    /// May be invoked before agent's first save; implementations MUST call EnsureDatabaseInitializedAsync internally.
    /// </summary>
    Task UpsertSharedVariableAsync(string conversationId, string variableName, object variableValue, string sourceAgentIntent);

    /// <summary>
    /// Loads all shared context variables that have been written to the conversation-scoped
    /// <c>shared_context</c> registry up to this point. Called by every agent at the start of
    /// each turn (after the agent's session is loaded/created) so that variables produced by
    /// any sibling agent — including ones that no longer exist as live actors — are available
    /// to the current agent's tools.
    /// </summary>
    /// <param name="conversationId">Conversation identifier (used to locate the per-conversation DB).</param>
    /// <returns>
    /// Dictionary of variable name → value. Empty dictionary when the conversation has no
    /// shared variables yet (or when the database does not yet exist).
    /// </returns>
    /// <remarks>
    /// The caller is expected to feed the returned dictionary into
    /// <see cref="MorganaAIContextProvider.MergeSharedContext"/>, which itself enforces
    /// first-write-wins on the agent-local side: variables already in the agent's own session
    /// are not overwritten by the registry.
    /// </remarks>
    Task<Dictionary<string, object>> LoadSharedVariablesAsync(string conversationId);

    /// <summary>
    /// Reports whether conversation exists in store. Restore path uses this to distinguish
    /// genuine existing conversations from stale identifiers never materialized.
    /// Backend-agnostic: SQLite checks DB file, SQL/PostgreSQL probe table, blob-store probes object.
    /// </summary>
    /// <param name="conversationId">Conversation identifier.</param>
    /// <returns><c>true</c> if the conversation is present in the store, <c>false</c> otherwise.</returns>
    bool ConversationExists(string conversationId);
}