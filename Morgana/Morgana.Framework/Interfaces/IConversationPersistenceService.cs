using System.Text.Json;
using Microsoft.Agents.AI;
using static Morgana.Framework.Records;

namespace Morgana.Framework.Interfaces;

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
    /// Current version: 2 (adds rate_limit_log table).</para>
    /// </remarks>
    Task EnsureDatabaseInitializedAsync(string conversationId);
}