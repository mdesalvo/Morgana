using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Morgana.AI.Providers;

/// <summary>
/// Keeps an agent's whole history in its session and hands the model either all of it or the reducer's view of it.
/// The reducer works on a copy for each run, so the stored history, which the record and the transcript are read from, is never shortened.
/// </summary>
internal class MorganaChatHistoryProvider : ChatHistoryProvider
{
    /// <summary>Writes what each run is handed and what each turn stores.</summary>
    private readonly ILogger logger;

    /// <summary>Shortens the copy of the history a run is handed; <c>null</c> hands the model everything.</summary>
    private readonly IChatReducer? viewReducer;

    /// <summary>The intent of the agent whose history this is, named in the log lines.</summary>
    private readonly string agentIntent;

    /// <summary>Keeps the <see cref="MorganaHistoryState"/> inside each session, so it is saved and restored with it.</summary>
    private readonly ProviderSessionState<MorganaHistoryState> sessionState;

    /// <summary>The key this provider's state is kept under in the session.</summary>
    public override IReadOnlyList<string> StateKeys => [ nameof(MorganaChatHistoryProvider) ];

    /// <summary>Binds the provider to its agent and to the reducer that shortens what the model reads.</summary>
    /// <param name="agentIntent">The intent of the agent, such as <c>billing</c>.</param>
    /// <param name="chatReducer">The reducer of the model's view; <c>null</c> hands the model the whole history.</param>
    /// <param name="logger">Writes the provider's log lines.</param>
    public MorganaChatHistoryProvider(
        string agentIntent,
        IChatReducer? chatReducer,
        ILogger logger)
    {
        this.agentIntent = agentIntent;
        viewReducer = chatReducer;
        this.logger = logger;

        // Whether this agent's model reads a reduced view, said once when the agent is created.
        string reducerInfo = chatReducer != null
            ? $"with view-reducer={chatReducer.GetType().Name}"
            : "without reducer";

        logger.LogInformation(
            "{MorganaChatHistoryProviderName} CREATED {ReducerInfo} for agent '{AgentIntent}'", nameof(MorganaChatHistoryProvider), reducerInfo, agentIntent);

        // The history lives in the session under this provider's key, serialized as Microsoft.Agents.AI serializes the rest of the session.
        sessionState = new ProviderSessionState<MorganaHistoryState>(
            stateInitializer: _ => new MorganaHistoryState(),
            stateKey: StateKeys[0],
            jsonSerializerOptions: AgentAbstractionsJsonUtilities.DefaultOptions);
    }

    /// <summary>Returns the session's whole history, never the reduced view.</summary>
    public List<ChatMessage> GetMessages(AgentSession session) =>
        sessionState.GetOrInitializeState(session).Messages;

    /// <summary>Files a message into the history outside a run of the agent: the user's phrase before the turn and the tool calls of a turn that was stopped.</summary>
    public void AppendMessage(AgentSession session, ChatMessage message)
    {
        // Filed in the session at once, so the next run's model reads the message in its place in the history.
        MorganaHistoryState historyState = sessionState.GetOrInitializeState(session);
        historyState.Messages.Add(message);
        sessionState.SaveState(session, historyState);

        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} FILED a {{Role}} message outside a run " +
            $"— total history: {{Count}} for agent '{{AgentIntent}}'",
            message.Role, historyState.Messages.Count, agentIntent);
    }

    // =========================================================================
    // ChatHistoryProvider overrides
    // =========================================================================

    /// <summary>Hands the run the history its model reads: the reducer's view when one is configured, the whole history otherwise.</summary>
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // Each agent keeps its history in its own session, so two agents of one conversation each read only what they were told.
        MorganaHistoryState historyState = sessionState.GetOrInitializeState(context.Session);
        List<ChatMessage> fullMessageHistory = historyState.Messages;

        // With a reducer the model reads a shortened copy, while the stored list stays whole.
        if (viewReducer != null)
        {
            List<ChatMessage> reducedView = [.. await viewReducer.ReduceAsync(fullMessageHistory, cancellationToken)];

            logger.LogInformation(
                $"{nameof(MorganaChatHistoryProvider)} PROVIDING reduced view " +
                $"({fullMessageHistory.Count} → {reducedView.Count} messages) for LLM context in agent '{agentIntent}'");

            // The model reads the summary and the recent window, while the stored list keeps every message for the record.
            return reducedView;
        }

        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} PROVIDING all {fullMessageHistory.Count} messages (no reducer) for agent '{agentIntent}'");

        // Without a reducer the model reads everything the agent was ever told.
        return fullMessageHistory;
    }

    /// <summary>Appends the turn's new messages to the whole history, dating every response message on Morgana's clock.</summary>
    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        // The history the turn joins, saved with the agent's session on its row of the record.
        MorganaHistoryState historyState = sessionState.GetOrInitializeState(context.Session);

        // The base class filters context.RequestMessages to exclude messages already in chat history,
        // so only the new user/tool messages for this turn arrive here.
        List<ChatMessage> newMessages = [.. context.RequestMessages, .. context.ResponseMessages ?? []];

        // Every response message carries Morgana's own clock, whatever the provider stamped or left
        // unstamped: the history orders turns by it and a client catching up after a disconnection
        // compares it with the timestamps of the replies it was delivered.
        int responseStartIndex = context.RequestMessages?.Count() ?? 0;
        for (int messageIndex = responseStartIndex; messageIndex < newMessages.Count; messageIndex++)
            newMessages[messageIndex].CreatedAt = DateTimeOffset.UtcNow;

        // Request before response, in the order the model will read them back.
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

    /// <summary>One session's whole history, saved and restored with the session.</summary>
    public sealed class MorganaHistoryState
    {
        /// <summary>Every message the agent was told and wrote, in order; the reducer never shortens it.</summary>
        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = [];
    }
}