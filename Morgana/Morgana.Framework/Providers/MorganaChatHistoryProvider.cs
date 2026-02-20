using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Morgana.Framework.Providers;

/// <summary>
/// Chat history provider that decouples storage from the LLM context view.
/// The complete conversation history is always preserved in <see cref="AgentSession"/>,
/// while an optional <see cref="IChatReducer"/> produces a condensed view sent to the LLM.
/// </summary>
/// <remarks>
/// <para>One instance is created per agent intent and shared across all sessions of that agent.
/// Per-session data (the full message list) lives in <see cref="AgentSession"/> via
/// <see cref="ProviderSessionState{T}"/> and is serialized automatically by the framework.</para>
///
/// <para><strong>Storage vs. LLM view:</strong></para>
/// <list type="bullet">
/// <item><term>Storage</term><description>All messages are appended to <c>MorganaHistoryState.Messages</c> in AgentSession. The reducer never touches this list.</description></item>
/// <item><term>LLM view</term><description>If a reducer is configured, a temporary reduced copy is computed before each invocation and discarded afterward.</description></item>
/// <item><term>UI / diagnostics</term><description>Consumers can read the unmodified full history via <see cref="GetMessages"/>.</description></item>
/// </list>
/// </remarks>
public class MorganaChatHistoryProvider : ChatHistoryProvider
{
    /// <summary>
    /// Optional reducer applied to produce an optimized context window for the LLM.
    /// Never modifies the stored history.
    /// </summary>
    private readonly IChatReducer? viewReducer;

    /// <summary>Agent intent label used in log output.</summary>
    private readonly string agentIntent;

    private readonly ILogger logger;

    /// <summary>
    /// Manages storage and retrieval of <see cref="MorganaHistoryState"/> within <see cref="AgentSession"/>.
    /// </summary>
    private readonly ProviderSessionState<MorganaHistoryState> sessionState;

    /// <summary>
    /// Key used by the framework to store and retrieve this provider's state within <see cref="AgentSession"/>.
    /// </summary>
    public override string StateKey => nameof(MorganaChatHistoryProvider);

    /// <summary>
    /// Initializes a new singleton instance of <see cref="MorganaChatHistoryProvider"/>.
    /// </summary>
    /// <param name="agentIntent">Agent intent label (e.g. "billing") used in log output.</param>
    /// <param name="chatReducer">Reducer used only for LLM context optimization. Pass <c>null</c> to disable reduction.</param>
    /// <param name="logger">Logger for diagnostics.</param>
    /// <param name="jsonSerializerOptions">
    /// JSON serialization options for state persistence.
    /// Defaults to <c>AgentAbstractionsJsonUtilities.DefaultOptions</c>.
    /// </param>
    public MorganaChatHistoryProvider(
        string agentIntent,
        IChatReducer? chatReducer,
        ILogger logger,
        JsonSerializerOptions? jsonSerializerOptions = null)
    {
        this.agentIntent = agentIntent;
        this.viewReducer = chatReducer;
        this.logger = logger;

        string reducerInfo = chatReducer != null
            ? $"with view-reducer={chatReducer.GetType().Name}"
            : "without reducer";

        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} CREATED {reducerInfo} for agent '{agentIntent}'");

        sessionState = new ProviderSessionState<MorganaHistoryState>(
            stateInitializer: _ => new MorganaHistoryState(),
            stateKey: StateKey,
            jsonSerializerOptions: jsonSerializerOptions ?? AgentAbstractionsJsonUtilities.DefaultOptions);
    }

    /// <summary>
    /// Returns the complete, unreduced conversation history for the given session.
    /// Useful for UI display or audit. Never returns a reduced view.
    /// </summary>
    public List<ChatMessage> GetMessages(AgentSession session) =>
        sessionState.GetOrInitializeState(session).Messages;

    // =========================================================================
    // ChatHistoryProvider overrides
    // =========================================================================

    /// <summary>
    /// Called BEFAORE each agent invocation to supply conversation history to the LLM.
    /// Returns a reduced view if a reducer is configured; otherwise returns the full history.
    /// The stored history is never modified.
    /// </summary>
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        MorganaHistoryState historyState = sessionState.GetOrInitializeState(context.Session);
        List<ChatMessage> fullMessageHistory = historyState.Messages;

        string sessionId = context.Session?.ToString() ?? "?";

        if (viewReducer != null)
        {
            List<ChatMessage> reducedView = (await viewReducer.ReduceAsync(fullMessageHistory, cancellationToken)).ToList();

            logger.LogInformation(
                $"{nameof(MorganaChatHistoryProvider)} PROVIDING reduced view " +
                $"({fullMessageHistory.Count} → {reducedView.Count} messages) for LLM context " +
                $"in agent '{agentIntent}' session '{sessionId}'");

            return reducedView;
        }

        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} PROVIDING all {fullMessageHistory.Count} messages (no reducer) " +
            $"for agent '{agentIntent}' session '{sessionId}'");

        return fullMessageHistory;
    }

    /// <summary>
    /// Called AFTER agent invocation to persist new messages.
    /// Appends only the new turn messages (request + response) to the full history.
    /// Reduction is never applied to storage.
    /// </summary>
    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        MorganaHistoryState historyState = sessionState.GetOrInitializeState(context.Session);

        // The base class filters context.RequestMessages to exclude messages already in chat history,
        // so only the new user/tool messages for this turn arrive here.
        List<ChatMessage> newMessages = context.RequestMessages
            .Concat(context.ResponseMessages ?? [])
            .ToList();

        // Stamp response messages with a server-side UTC timestamp.
        int responseStartIndex = context.RequestMessages?.Count() ?? 0;
        for (int i = responseStartIndex; i < newMessages.Count; i++)
        {
            if (newMessages[i].CreatedAt.HasValue)
                newMessages[i].CreatedAt = DateTimeOffset.UtcNow;
        }

        historyState.Messages.AddRange(newMessages);
        sessionState.SaveState(context.Session, historyState);

        string sessionId = context.Session?.ToString() ?? "?";
        int requestCount = context.RequestMessages?.Count() ?? 0;
        int responseCount = context.ResponseMessages?.Count() ?? 0;

        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} STORED {newMessages.Count} messages " +
            $"(request: {requestCount}, response: {responseCount}) — total history: {historyState.Messages.Count} " +
            $"for agent '{agentIntent}' session '{sessionId}'");

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Per-session state stored inside <see cref="AgentSession"/> via <see cref="ProviderSessionState{T}"/>.
    /// Serialized and restored automatically by the framework as part of session persistence.
    /// </summary>
    public sealed class MorganaHistoryState
    {
        /// <summary>
        /// Complete conversation message list for this session.
        /// Never modified by the reducer — the reducer operates only on a temporary copy.
        /// </summary>
        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = [];
    }
}