using Microsoft.Agents.AI;
using System.Text.Json;

namespace Morgana.Framework.Interfaces;

/// <summary>
/// Service for persisting and loading conversation state (AgentThread) across application restarts.
/// Enables resuming conversations from where they left off by serializing thread state to durable storage.
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
/// <item><term>Chat Message Store State</term><description>Internal state of the message store</description></item>
/// </list>
/// <para><strong>Usage Pattern:</strong></para>
/// <code>
/// // Save conversation after each turn
/// await persistenceService.SaveConversationAsync(conversationId, agentThread);
///
/// // Load conversation on subsequent requests
/// AgentThread? restored = await persistenceService.LoadConversationAsync(conversationId, morganaAgent);
/// if (restored != null)
/// {
///     // Continue conversation with restored state
///     await aiAgent.RunAsync(userMessage, restored);
/// }
/// </code>
/// <para><strong>Implementation Examples:</strong></para>
/// <list type="bullet">
/// <item><term>EncryptedFileConversationPersistenceService</term><description>Stores conversations in encrypted .morgana.json files</description></item>
/// <item><term>SqlConversationPersistenceService</term><description>Stores conversations in SQL database (future)</description></item>
/// <item><term>CosmosDbConversationPersistenceService</term><description>Stores conversations in Azure Cosmos DB (future)</description></item>
/// </list>
/// </remarks>
public interface IConversationPersistenceService
{
    /// <summary>
    /// Saves the complete conversation state to persistent storage.
    /// Serializes the AgentThread including message history, context variables, and metadata.
    /// </summary>
    /// <param name="agentIdentifier">Unique identifier for the agent's conversation</param>
    /// <param name="agentThread">AgentThread instance containing the complete conversation state</param>
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
    Task SaveConversationAsync(
        string agentIdentifier,
        AgentThread agentThread,
        JsonSerializerOptions? jsonSerializerOptions = null);

    /// <summary>
    /// Loads a previously saved agent's conversation state from persistent storage.
    /// Deserializes the AgentThread and reconnects all context providers and callbacks.
    /// </summary>
    /// <param name="agentIdentifier">Unique identifier for the agent's conversation to load</param>
    /// <param name="agent">MorganaAgent instance that will receive the deserialized thread</param>
    /// <param name="jsonSerializerOptions">JSON serialization options (optional, uses AgentAbstractionsJsonUtilities.DefaultOptions if null)</param>
    /// <returns>Deserialized AgentThread if conversation exists, null if not found</returns>
    /// <remarks>
    /// <para><strong>Null Return Semantics:</strong></para>
    /// <para>Returns null when the agentIdentifier has never been saved, indicating this is a new conversation.
    /// Callers should create a new AgentThread in this case via agent.GetNewThread().</para>
    /// <para><strong>Deserialization Process:</strong></para>
    /// <list type="number">
    /// <item>Read and decrypt (if applicable) the serialized thread data</item>
    /// <item>Deserialize JSON to JsonElement</item>
    /// <item>Call agent.DeserializeThread() to reconstruct the full thread state</item>
    /// <item>Return the fully restored AgentThread</item>
    /// </list>
    /// </remarks>
    Task<AgentThread?> LoadConversationAsync(
        string agentIdentifier,
        Abstractions.MorganaAgent agent,
        JsonSerializerOptions? jsonSerializerOptions = null);
}