using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Morgana.Framework.Providers;

// This suppresses the experimental API warning for IChatReducer usage.
// Microsoft marks IChatReducer as experimental (MEAI001) but recommends it
// for production use in context window management scenarios.
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates

/// <summary>
/// Morgana's custom chat history provider that decouples storage from LLM view.
/// Unlike Microsoft's InMemoryChatHistoryProvider, this implementation preserves full conversation
/// history in storage while using IChatReducer ONLY to optimize the context sent to the LLM.
/// </summary>
/// <para><strong>Architecture:</strong></para>
/// <list type="bullet">
/// <item><term>fullHistory</term><description>Complete message storage, never modified by reducer</description></item>
/// <item><term>Reducer</term><description>Creates temporary reduced view for LLM context only</description></item>
/// <item><term>Serialization</term><description>Always persists fullHistory (complete conversation)</description></item>
/// <item><term>UI</term><description>Always displays fullHistory (user sees everything)</description></item>
/// <item><term>Logging</term><description>Tracks operations with conversationId and intent context</description></item>
/// </list>
public class MorganaChatHistoryProvider : ChatHistoryProvider
{
    /// <summary>
    /// Complete conversation history. This list is NEVER modified by the reducer.
    /// All new messages are appended here, and this is what gets serialized to storage.
    /// </summary>
    private List<ChatMessage> fullHistory = [];

    /// <summary>
    /// Optional reducer for creating optimized LLM context views.
    /// Uses Microsoft's SummarizingChatReducer algorithms (smart cutoff points, tool message preservation)
    /// but ONLY for temporary views - never modifies fullHistory.
    /// </summary>
    private readonly IChatReducer? viewReducer;

    private readonly string conversationId;
    private readonly string intent;
    private readonly ILogger logger;

    /// <summary>
    /// Creates a new chat history provider.
    /// If serializedState is provided, deserializes full history (conversation resumption).
    /// If serializedState is empty/undefined, starts with empty history (new conversation).
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation</param>
    /// <param name="intent">Agent intent (e.g., "billing", "contract")</param>
    /// <param name="serializedState">Serialized state from previous session, or default/undefined for new conversation</param>
    /// <param name="jsonSerializerOptions">JSON serialization options from framework</param>
    /// <param name="chatReducer">Reducer used ONLY for LLM context optimization (never modifies storage)</param>
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
        this.viewReducer = chatReducer;
        this.logger = logger;

        // Deserialize full history from storage (if present)
        // Format: { "Messages": [...] } (Microsoft-compatible)
        if (serializedState.ValueKind is JsonValueKind.Object)
        {
            JsonSerializerOptions jso = jsonSerializerOptions ?? AgentAbstractionsJsonUtilities.DefaultOptions;

            if (serializedState.TryGetProperty("messages", out JsonElement messagesElement))
            {
                List<ChatMessage>? messages = messagesElement.Deserialize<List<ChatMessage>>(jso);
                if (messages != null)
                    fullHistory = messages;
            }
        }

        // Log creation/restoration
        string reducerInfo = chatReducer != null
            ? $"with view-reducer={chatReducer.GetType().Name}"
            : "without reducer";

        logger.LogInformation(
            serializedState.ValueKind != JsonValueKind.Undefined
                ? $"{nameof(MorganaChatHistoryProvider)} RESTORED {fullHistory.Count} messages {reducerInfo} for agent '{intent}' in conversation '{conversationId}'"
                : $"{nameof(MorganaChatHistoryProvider)} CREATED {reducerInfo} for agent '{intent}' in conversation '{conversationId}'");
    }

    /// <summary>
    /// Called BEFORE agent invocation to retrieve messages for LLM context.
    /// If reducer is configured, creates a TEMPORARY reduced view using Microsoft's smart algorithms.
    /// IMPORTANT: fullHistory is NEVER modified - reduction is only for LLM consumption.
    /// </summary>
    public override async ValueTask<IEnumerable<ChatMessage>> InvokingAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // If reducer configured, create temporary optimized view for LLM
        if (viewReducer != null)
        {
            List<ChatMessage> reducedView = (await viewReducer.ReduceAsync(fullHistory, cancellationToken)).ToList();

            logger.LogInformation(
                $"{nameof(MorganaChatHistoryProvider)} INVOKING: Created reduced view " +
                $"({fullHistory.Count} → {reducedView.Count} messages) for LLM context " +
                $"in agent '{intent}' conversation '{conversationId}'");

            // Return temporary reduced view (LLM sees optimized context, but storage stays intact)
            return reducedView;
        }

        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} INVOKING: Returning all {fullHistory.Count} messages (no reducer) " +
            $"for agent '{intent}' in conversation '{conversationId}'");

        // No reducer: return full history
        return fullHistory;
    }

    /// <summary>
    /// Called AFTER agent invocation to store new messages.
    /// ALWAYS appends to fullHistory - NEVER applies reduction to storage.
    /// This ensures complete conversation history is preserved across sessions.
    /// </summary>
    public override ValueTask InvokedAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.InvokeException is not null)
        {
            logger.LogWarning(
                $"{nameof(MorganaChatHistoryProvider)} INVOKED: Skipping storage due to exception " +
                $"in agent '{intent}' conversation '{conversationId}'");

            return ValueTask.CompletedTask;
        }

        // Collect all new messages (request + response)
        List<ChatMessage> newMessages = context.RequestMessages.Concat(context.ResponseMessages ?? []).ToList();

        // Override timestamps for response messages
        int responseStartIndex = context.RequestMessages?.Count() ?? 0;
        for (int i = responseStartIndex; i < newMessages.Count; i++)
        {
            if (newMessages[i].CreatedAt.HasValue)
                newMessages[i].CreatedAt = DateTime.UtcNow;
        }

        // Append to full history WITHOUT any reduction
        fullHistory.AddRange(newMessages);

        int requestCount = context.RequestMessages?.Count() ?? 0;
        int responseCount = context.ResponseMessages?.Count() ?? 0;

        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} INVOKED: Stored {newMessages.Count} messages " +
            $"(request: {requestCount}, response: {responseCount}) - total history: {fullHistory.Count} " +
            $"for agent '{intent}' in conversation '{conversationId}'");

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Serializes provider state for AgentSession persistence.
    /// ALWAYS serializes fullHistory (complete conversation), never reduced views.
    /// Format: { "Messages": [...] } for extensibility and Microsoft compatibility.
    /// </summary>
    public override JsonElement Serialize(JsonSerializerOptions? jsonSerializerOptions = null)
    {
        JsonSerializerOptions jso = jsonSerializerOptions ?? AgentAbstractionsJsonUtilities.DefaultOptions;

        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} SERIALIZING {fullHistory.Count} messages to storage " +
            $"for agent '{intent}' in conversation '{conversationId}'");

        // Serialize with Microsoft-compatible format: { "Messages": [...] }
        // This allows for future extensibility (e.g., adding metadata)
        return JsonSerializer.SerializeToElement(new { Messages = fullHistory }, jso);
    }
}