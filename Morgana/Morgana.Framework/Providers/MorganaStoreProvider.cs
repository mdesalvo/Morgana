using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Morgana.Framework.Providers;

/// <summary>
/// Morgana wrapper around Microsoft's InMemoryChatMessageStore that adds conversation tracking and logging.
/// Delegates complex message management to the framework while providing Morgana-specific observability.
/// </summary>
/// <remarks>
/// <para><strong>Architecture:</strong></para>
/// <code>
/// MorganaStoreProvider (Morgana layer)
///   └── InMemoryChatMessageStore (Microsoft implementation)
///         └── Handles message storage, filtering, serialization
/// </code>
/// <para><strong>Responsibilities:</strong></para>
/// <list type="bullet">
/// <item><term>Logging</term><description>Tracks message operations with conversationId and intent context</description></item>
/// <item><term>Metadata</term><description>Associates messages with specific conversation and agent</description></item>
/// <item><term>Delegation</term><description>Leverages Microsoft's tested implementation for core functionality</description></item>
/// </list>
/// <para><strong>Integration with Framework:</strong></para>
/// <code>
/// AgentThread.Serialize() → combines:
///   - MorganaStoreProvider.Serialize() → InMemoryChatMessageStore.Serialize()
///   - MorganaContextProvider.Serialize()
///   
/// AgentThread.Deserialize() → restores both via factories
/// </code>
/// <para><strong>Future Extensibility:</strong></para>
/// <para>When database persistence is needed, replace innerStore with DatabaseChatMessageStore
/// while keeping the same Morgana wrapper interface.</para>
/// </remarks>
public class MorganaStoreProvider : ChatMessageStore
{
    private readonly InMemoryChatMessageStore innerStore;
    private readonly string conversationId;
    private readonly string intent;
    private readonly ILogger logger;

    /// <summary>
    /// Creates a new message store provider that wraps Microsoft's InMemoryChatMessageStore.
    /// Used by ChatMessageStoreFactory during thread creation/resumption.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation</param>
    /// <param name="intent">Agent intent (e.g., "billing", "contract")</param>
    /// <param name="serializedState">Serialized state from previous thread, or null for new thread</param>
    /// <param name="jsonSerializerOptions">JSON serialization options from framework</param>
    /// <param name="logger">Logger instance for diagnostics</param>
    public MorganaStoreProvider(
        string conversationId,
        string intent,
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions,
        ILogger logger)
    {
        this.conversationId = conversationId;
        this.intent = intent;
        this.logger = logger;
        
        // Delegate to Microsoft's battle-tested implementation
        innerStore = new InMemoryChatMessageStore(
            serializedStoreState: serializedState,
            jsonSerializerOptions: jsonSerializerOptions);
        
        logger.LogInformation(
            serializedState.ValueKind != JsonValueKind.Undefined
                ? $"{nameof(MorganaStoreProvider)} RESTORED for agent '{intent}' in conversation '{conversationId}'"
                : $"{nameof(MorganaStoreProvider)} CREATED for agent '{intent}' in conversation '{conversationId}'");
    }

    /// <summary>
    /// Called BEFORE agent invocation to retrieve messages for LLM context.
    /// Delegates to Microsoft's InMemoryChatMessageStore for actual retrieval.
    /// </summary>
    public override async ValueTask<IEnumerable<ChatMessage>> InvokingAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<ChatMessage> messages = await innerStore.InvokingAsync(context, cancellationToken);
        
        logger.LogInformation(
            $"{nameof(MorganaStoreProvider)} INVOKING: Returning {messages.Count()} messages for agent '{intent}' in conversation '{conversationId}'");
        
        return messages;
    }

    /// <summary>
    /// Called AFTER agent invocation to store new messages.
    /// Delegates to Microsoft's InMemoryChatMessageStore for actual storage.
    /// </summary>
    public override async ValueTask InvokedAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        await innerStore.InvokedAsync(context, cancellationToken);
        
        // Log after successful storage
        int requestCount = context.RequestMessages?.Count() ?? 0;
        int responseCount = context.ResponseMessages?.Count() ?? 0;
        int contextProviderCount = context.AIContextProviderMessages?.Count() ?? 0;
        int totalCount = requestCount + responseCount + contextProviderCount;
        
        logger.LogInformation(
            $"{nameof(MorganaStoreProvider)} INVOKED: Stored {totalCount} messages " +
            $"(request: {requestCount}, response: {responseCount}, context: {contextProviderCount}) " +
            $"for agent '{intent}' in conversation '{conversationId}'");
    }

    /// <summary>
    /// Serializes message store state for AgentThread persistence.
    /// Delegates to Microsoft's InMemoryChatMessageStore for actual serialization.
    /// Framework automatically combines this with MorganaContextProvider state.
    /// </summary>
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        JsonElement serialized = innerStore.Serialize(jsonSerializerOptions);
        
        logger.LogInformation(
            $"{nameof(MorganaStoreProvider)} SERIALIZED for agent '{intent}' in conversation '{conversationId}'");
        
        return serialized;
    }
}