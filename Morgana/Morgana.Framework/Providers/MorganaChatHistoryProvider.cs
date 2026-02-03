using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Morgana.Framework.Providers;

// This suppresses the experimental API warning for IChatReducer usage.
// Microsoft marks IChatReducer as experimental (MEAI001) but recommends it
// for production use in context window management scenarios.
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates

/// <summary>
/// Morgana wrapper around Microsoft's in-memory implementation that adds conversation tracking and logging.
/// Delegates complex message management to the framework while providing Morgana-specific observability.
/// </summary>
/// <remarks>
/// <para><strong>Architecture:</strong></para>
/// <code>
/// MorganaChatHistoryProvider (Morgana layer)
///   └── InMemoryChatHistoryProvider (Microsoft implementation)
///         ├── Optional: IChatReducer (context window management)
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
/// AgentSession.Serialize() → combines:
///   - MorganaChatHistoryProvider.Serialize() → InMemoryChatHistoryProvider.Serialize()
///   - MorganaAIContextProvider.Serialize()
///
/// AgentSession.Deserialize() → restores both via factories
/// </code>
/// </remarks>
public class MorganaChatHistoryProvider : ChatHistoryProvider
{
    private readonly InMemoryChatHistoryProvider innerProvider;
    private readonly string conversationId;
    private readonly string intent;
    private readonly ILogger logger;

    /// <summary>
    /// Creates a new chat history provider that wraps Microsoft's in-memory implementation.
    /// Used by ChatHistoryProviderFactory during session creation/resumption.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation</param>
    /// <param name="intent">Agent intent (e.g., "billing", "contract")</param>
    /// <param name="serializedState">Serialized state from previous session, or null for new session</param>
    /// <param name="jsonSerializerOptions">JSON serialization options from framework</param>
    /// <param name="logger">Logger instance for diagnostics</param>
    public MorganaChatHistoryProvider(
        string conversationId,
        string intent,
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions,
        IChatReducer? chatReducer,
        ILogger logger)
    {
        this.conversationId = conversationId;
        this.intent = intent;
        this.logger = logger;

        // Delegate to Microsoft's in-memory implementation:
        // the reducer (if given) will operate on InvokingAsync hint,
        // so that LLM will immediately receive compacted history.
        // This is perfect for context window management.
        innerProvider = new InMemoryChatHistoryProvider(
            chatReducer: chatReducer,
            serializedState: serializedState,
            jsonSerializerOptions: jsonSerializerOptions,
            reducerTriggerEvent: InMemoryChatHistoryProvider.ChatReducerTriggerEvent.BeforeMessagesRetrieval);

        string reducerInfo = chatReducer != null ? $"with reducer={chatReducer.GetType().Name}" : "without reducer";

        logger.LogInformation(
            serializedState.ValueKind != JsonValueKind.Undefined
                ? $"{nameof(MorganaChatHistoryProvider)} RESTORED {reducerInfo} for agent '{intent}' in conversation '{conversationId}'"
                : $"{nameof(MorganaChatHistoryProvider)} CREATED {reducerInfo} for agent '{intent}' in conversation '{conversationId}'");
    }

    /// <summary>
    /// Called BEFORE agent invocation to retrieve messages for LLM context.
    /// Delegates to Microsoft's in-memory implementation for actual retrieval.
    /// If a reducer is configured, it will process the messages here.
    /// </summary>
    public override async ValueTask<IEnumerable<ChatMessage>> InvokingAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<ChatMessage> messages = await innerProvider.InvokingAsync(context, cancellationToken);

        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} INVOKING: Returning {messages.Count()} messages for agent '{intent}' in conversation '{conversationId}'");

        return messages;
    }

    /// <summary>
    /// Called AFTER agent invocation to store new messages.
    /// Delegates to Microsoft's in-memory implementation for actual storage.
    /// </summary>
    public override async ValueTask InvokedAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        // Override timestamp of LLM response messages (if valorized).
        foreach (ChatMessage responseMessage in context.ResponseMessages ?? [])
        {
            if (responseMessage.CreatedAt.HasValue)
                responseMessage.CreatedAt = DateTime.UtcNow;
        }

        await innerProvider.InvokedAsync(context, cancellationToken);

        // Log after successful storage
        int requestCount = context.RequestMessages?.Count() ?? 0;
        int responseCount = context.ResponseMessages?.Count() ?? 0;
        int contextProviderCount = context.AIContextProviderMessages?.Count() ?? 0;
        int totalCount = requestCount + responseCount + contextProviderCount;

        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} INVOKED: Stored {totalCount} messages " +
            $"(request: {requestCount}, response: {responseCount}, context: {contextProviderCount}) " +
            $"for agent '{intent}' in conversation '{conversationId}'");
    }

    /// <summary>
    /// Serializes chat history state for AgentSession persistence.
    /// Delegates to Microsoft's in-memory implementation for actual serialization.
    /// Framework automatically combines this with MorganaAIContextProvider state.
    /// </summary>
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        JsonElement serialized = innerProvider.Serialize(jsonSerializerOptions);

        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} SERIALIZED for agent '{intent}' in conversation '{conversationId}'");

        return serialized;
    }
}