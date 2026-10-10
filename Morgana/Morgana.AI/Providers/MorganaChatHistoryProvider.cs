using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Providers;

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
/// <item><term>LLM view</term><description>The current episode only — what followed the last turn that the user left on — reduced if a reducer is configured, with every tool result of an earlier turn marked as such. Computed before each invocation and discarded afterward.</description></item>
/// <item><term>UI / diagnostics</term><description>Consumers can read the unmodified full history via <see cref="GetMessages"/>.</description></item>
/// </list>
/// </remarks>
public class MorganaChatHistoryProvider : ChatHistoryProvider
{
    /// <summary>Logger for provider-level diagnostics.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Optional reducer applied to produce an optimized context window for the LLM.
    /// Never modifies the stored history.
    /// </summary>
    private readonly IChatReducer? viewReducer;

    /// <summary>Agent intent label used in log output.</summary>
    private readonly string agentIntent;

    /// <summary>
    /// Marks the tool results of earlier turns in the view. Null leaves them as they were returned.
    /// </summary>
    private readonly IPromptComposerService? promptComposerService;

    /// <summary>
    /// Manages storage and retrieval of <see cref="MorganaHistoryState"/> within <see cref="AgentSession"/>.
    /// </summary>
    private readonly ProviderSessionState<MorganaHistoryState> sessionState;

    /// <summary>
    /// Keys used by the framework to store and retrieve this provider's state within <see cref="AgentSession"/>.
    /// </summary>
    public override IReadOnlyList<string> StateKeys => [ nameof(MorganaChatHistoryProvider) ];

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
    /// <param name="promptComposerService">Marks earlier tool results in the view. Pass <c>null</c> to leave them unmarked.</param>
    public MorganaChatHistoryProvider(
        string agentIntent,
        IChatReducer? chatReducer,
        ILogger logger,
        JsonSerializerOptions? jsonSerializerOptions = null,
        IPromptComposerService? promptComposerService = null)
    {
        // The intent labels every log line, so that the lines of one agent can be told apart in a shared log.
        this.agentIntent = agentIntent;
        this.promptComposerService = promptComposerService;
        viewReducer = chatReducer;
        this.logger = logger;

        // The log line states whether the agent's view is condensed, which is what an operator reads it for.
        string reducerInfo = chatReducer != null
            ? $"with view-reducer={chatReducer.GetType().Name}"
            : "without reducer";

        logger.LogInformation(
            "{MorganaChatHistoryProviderName} CREATED {ReducerInfo} for agent '{AgentIntent}'", nameof(MorganaChatHistoryProvider), reducerInfo, agentIntent);

        // A session that holds no history yet starts with an empty list, so the first turn needs no special case.
        sessionState = new ProviderSessionState<MorganaHistoryState>(
            stateInitializer: _ => new MorganaHistoryState(),
            stateKey: StateKeys[0],
            jsonSerializerOptions: jsonSerializerOptions ?? AgentAbstractionsJsonUtilities.DefaultOptions);
    }

    /// <summary>
    /// Returns the complete, unreduced conversation history for the given session.
    /// Useful for UI display or audit. Never returns a reduced view.
    /// </summary>
    public List<ChatMessage> GetMessages(AgentSession session) =>
        sessionState.GetOrInitializeState(session).Messages;

    /// <summary>
    /// Files the user's message into the history before the turn runs, so it can be persisted
    /// ahead of the LLM instead of only once the turn closes.
    /// </summary>
    public void AppendMessage(AgentSession session, ChatMessage message)
    {
        // The message joins the stored history, which is the record the agent's session is persisted from.
        MorganaHistoryState historyState = sessionState.GetOrInitializeState(session);
        historyState.Messages.Add(message);

        // Saved at once: the caller persists the session before the model runs, so the message survives a failed turn.
        sessionState.SaveState(session, historyState);

        // The log line shows the history growing ahead of the turn.
        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} FILED an inbound {{Role}} message ahead of the turn " +
            $"— total history: {{Count}} for agent '{{AgentIntent}}'",
            message.Role, historyState.Messages.Count, agentIntent);
    }

    // =========================================================================
    // ChatHistoryProvider overrides
    // =========================================================================

    /// <summary>
    /// Called BEFORE each agent invocation to supply conversation history to the LLM: the current
    /// episode, reduced if a reducer is configured, its earlier tool results marked as such.
    /// The stored history is never modified.
    /// </summary>
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        // The stored history is the source of the view and stays untouched by it.
        MorganaHistoryState historyState = sessionState.GetOrInitializeState(context.Session);
        List<ChatMessage> fullMessageHistory = historyState.Messages;

        // Reduced on the stored messages themselves: the reducer stamps its summary onto one of them,
        // which is how the summary reaches the saved session.
        List<ChatMessage> episode = CurrentEpisode(fullMessageHistory);
        List<ChatMessage> view = viewReducer != null
            ? [.. await viewReducer.ReduceAsync(episode, cancellationToken)]
            : episode;

        // The log line compares what the model is handed with what is stored, which shows how much the view cut.
        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} PROVIDING {{ViewCount}} messages (history {{HistoryCount}}, episode {{EpisodeCount}}) for agent '{{AgentIntent}}'",
            view.Count, fullMessageHistory.Count, episode.Count, agentIntent);

        // The model reads the results of earlier turns as how things stood then, never as current facts.
        return await MarkEarlierToolResultsAsync(view);
    }

    /// <summary>
    /// The messages since the last turn that the user left on: the episode that a returning user opens. The
    /// same message instances as the history, so a fold stamped on them reaches the stored record.
    /// </summary>
    /// <param name="history">The agent's whole stored history.</param>
    public static List<ChatMessage> CurrentEpisode(IReadOnlyList<ChatMessage> history)
    {
        // The search runs from the newest message back: the latest farewell is the one that closes the previous episode.
        int lastEpisodeEnd = -1;
        for (int index = history.Count - 1; index >= 0 && lastEpisodeEnd < 0; index--)
        {
            if (history[index].AdditionalProperties?.ContainsKey(Constants.MessageProperties.EpisodeEnd) == true)
                lastEpisodeEnd = index;
        }

        // A user who never left has one episode: the whole history.
        if (lastEpisodeEnd < 0)
            return [.. history];

        // The farewell is stamped on the message that wrote it, while the results of the calls closing that
        // turn are filed after it. The episode opens at the returning user's message: a result handed over
        // without its call is refused by the provider.
        int episodeStart = lastEpisodeEnd + 1;
        while (episodeStart < history.Count && history[episodeStart].Role != ChatRole.User)
            episodeStart++;

        // The slice holds the very instances of the history, so that a fold stamped on them is stored.
        return [.. history.Skip(episodeStart)];
    }

    /// <summary>
    /// Wraps every tool result that came back before the turn now opening, so the model reads it as how
    /// things stood then. Copies are wrapped and the stored messages stay as the tools returned them.
    /// </summary>
    private async Task<IEnumerable<ChatMessage>> MarkEarlierToolResultsAsync(List<ChatMessage> view)
    {
        // Without a composer there is no wording for the marker: the results are handed over as returned.
        if (promptComposerService is null)
            return view;

        // The turn now opening starts at the user's message, filed before the run: a result after it
        // belongs to this turn, which happens only when the turn is run a second time.
        int currentTurnStart = view.FindLastIndex(message => message.Role == ChatRole.User);

        // The view is rebuilt message by message so that the stored instances are never altered.
        List<ChatMessage> marked = [];
        for (int index = 0; index < view.Count; index++)
        {
            // A message of the current turn or one that carries no tool result needs no marker and is handed over as it is.
            ChatMessage message = view[index];
            if (index >= currentTurnStart || !message.Contents.OfType<FunctionResultContent>().Any())
            {
                marked.Add(message);
                continue;
            }

            // Each result is wrapped under its own call id, so that the provider still pairs it with its call.
            List<AIContent> contents = [];
            foreach (AIContent content in message.Contents)
            {
                string? wrapped = content is FunctionResultContent result
                    ? await promptComposerService.ComposeEarlierToolResultAsync(ResultText(result.Result))
                    : null;
                contents.Add(wrapped is not null && content is FunctionResultContent original
                    ? new FunctionResultContent(original.CallId, wrapped)
                    : content);
            }

            // The copy keeps author, date, id and properties of the stored message: only its results differ.
            marked.Add(new ChatMessage(message.Role, contents)
            {
                AuthorName = message.AuthorName,
                CreatedAt = message.CreatedAt,
                MessageId = message.MessageId,
                AdditionalProperties = message.AdditionalProperties
            });
        }

        // The marked copies replace the stored messages in what the model reads.
        return marked;
    }

    /// <summary>
    /// A tool result as text: a tool's string comes back as a JSON string, anything else as its JSON.
    /// </summary>
    private static string ResultText(object? result) => result switch
    {
        null => string.Empty,
        string text => text,
        JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
        JsonElement element => element.GetRawText(),
        _ => JsonSerializer.Serialize(result)
    };

    /// <summary>
    /// Called AFTER agent invocation to persist new messages.
    /// Appends only the new turn messages (request + response) to the full history.
    /// Reduction is never applied to storage.
    /// </summary>
    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default)
    {
        // The new messages are appended to the stored history, never to the reduced view.
        MorganaHistoryState historyState = sessionState.GetOrInitializeState(context.Session);

        // The base class filters context.RequestMessages to exclude messages already in chat history,
        // so only the new user/tool messages for this turn arrive here.
        List<ChatMessage> newMessages = [.. context.RequestMessages, .. context.ResponseMessages ?? []];

        // Every response message carries Morgana's own clock, whatever the provider stamped or left
        // unstamped: the history orders turns by it and a client catching up after a disconnection
        // compares it with the timestamps of the replies it was delivered.
        int requestCount = context.RequestMessages?.Count() ?? 0;
        int responseStartIndex = requestCount;
        for (int i = responseStartIndex; i < newMessages.Count; i++)
            newMessages[i].CreatedAt = DateTimeOffset.UtcNow;

        // Request and response are stored in the order they happened, which is the order the model replays them in.
        historyState.Messages.AddRange(newMessages);
        sessionState.SaveState(context.Session, historyState);

        // The counts tell the log reader how much of the turn was stored and from which session.
        string sessionId = context.Session?.ToString() ?? "?";
        int responseCount = context.ResponseMessages?.Count() ?? 0;

        logger.LogInformation(
            $"{nameof(MorganaChatHistoryProvider)} STORED {newMessages.Count} messages " +
            $"(request: {requestCount}, response: {responseCount}) — total history: {historyState.Messages.Count} " +
            $"for agent '{agentIntent}' session '{sessionId}'");

        // Storage is synchronous: nothing is left to await.
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