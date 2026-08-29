using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Morgana.AI.Services;

// This suppresses the experimental API warning for IChatReducer usage.
// Microsoft marks IChatReducer as experimental (MEAI001) but recommends it
// for production use in context window management scenarios.
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates

/// <summary>
/// Builds the reducer that keeps an agent's history from outgrowing the model reading it.
/// </summary>
/// <remarks>
/// Reduction folds the oldest stretch of a conversation into a summary and leaves recent messages
/// verbatim. It changes only what the LLM sees — the stored transcript keeps everything — and the fold is
/// one-way: what the summary drops, the model never gets back. One reducer per agent, on that agent's own
/// tier client. No reducer at all when the section disables it, and the agent runs on its full history.
/// </remarks>
public class HistoryReducerService
{
    /// <summary>
    /// Read for the <c>Morgana:HistoryReducer</c> section on every create call, so a reducer
    /// reflects the configuration in force when the agent was built.
    /// </summary>
    private readonly IConfiguration configuration;

    /// <summary>
    /// Logger for the reducer's configuration at creation time and for the disabled case, which
    /// returns null and would otherwise be indistinguishable from a misconfiguration.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of HistoryReducerService.
    /// </summary>
    /// <param name="configuration">Application configuration for reading HistoryReducer settings</param>
    /// <param name="logger">Logger for diagnostics and monitoring</param>
    public HistoryReducerService(
        IConfiguration configuration,
        ILogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    /// <summary>
    /// Creates the reducer for one agent, or <c>null</c> when history reduction is disabled.
    /// </summary>
    /// <param name="chatClient">Chat client the summarization call will run on — the calling agent's own tier.</param>
    /// <returns>A configured <see cref="MorganaChatReducer"/>, or <c>null</c> to run without reduction.</returns>
    /// <remarks>
    /// The null return is not a failure signal: <c>MorganaChatHistoryProvider</c> reads it as "hand the
    /// LLM the full history", which is the only way to turn reduction off.
    /// </remarks>
    public IChatReducer? CreateReducer(IChatClient chatClient)
    {
        IConfigurationSection config = configuration.GetSection("Morgana:HistoryReducer");

        if (!config.GetValue("Enabled", true))
        {
            logger.LogInformation("History summarization disabled - no reducer created");
            return null;
        }

        // Defaults mirror the shipped appsettings.json: first reduction at 21 messages.
        int targetCount = config.GetValue<int>("SummarizationTargetCount", 8);
        int threshold = config.GetValue<int>("SummarizationThreshold", 12);

        // chatClient here is whichever tier the calling agent runs on — each agent gets its own
        // reducer instance sized to its own history, there is no shared/singleton reducer.
        MorganaChatReducer chatReducer = new MorganaChatReducer(chatClient, targetCount, threshold, logger);

        // Only overridden when configured: the reducer ships a sensible built-in default prompt, so an
        // unset/blank SummarizationPrompt should leave that alone rather than override it with an
        // empty string.
        string? summaryPrompt = config.GetValue<string?>("SummarizationPrompt");
        if (!string.IsNullOrWhiteSpace(summaryPrompt))
            chatReducer.SummarizationPrompt = summaryPrompt;

        logger.LogInformation(
            "Created MorganaChatReducer: target={TargetCount}, threshold(buffer)={Threshold} → reduction triggers when message count > {Trigger}",
            targetCount, threshold, targetCount + threshold);

        return chatReducer;
    }
}

/// <summary>
/// Summarizing chat reducer that keeps tool activity visible to the summarizer.
/// </summary>
/// <remarks>
/// <para>MEAI's <c>SummarizingChatReducer</c> drops every message carrying function-call or
/// function-result content from the summarizer's input, so the summarizing model sees a text-only
/// skeleton: tool returns, rich card contents and one-shot identifiers are all invisible, and it
/// reports — truthfully for its view — that no tool ran and no identifier exists. That summary then
/// replaces the messages it came from.</para>
/// <para>Only the summarizer's input differs here. Which messages get summarized is a faithful port,
/// see <see cref="SummarizedConversation"/>, and the kept window is handed back untouched. The
/// <c>__summary__</c> name is MEAI's, so sessions summarized before this shipped still resume.</para>
/// </remarks>
public sealed class MorganaChatReducer : IChatReducer
{
    /// <summary>Property stamped on the last summarized message. Same name MEAI uses, which is what makes the two interchangeable.</summary>
    public const string SummaryKey = Constants.MessageProperties.Summary;

    /// <summary>
    /// Cap on the rendered length of one content item, so a catalogue-sized tool return cannot dominate
    /// the call. Generous: identifiers are short and the point is that they survive.
    /// </summary>
    private const int MaxRenderedContentLength = 4000;

    /// <summary>
    /// Client the summarization call runs on. It is the calling agent's own tier, so the cost of
    /// summarizing lands on the agent whose history grew.
    /// </summary>
    private readonly IChatClient chatClient;

    /// <summary>
    /// Messages left unsummarized behind the cut. Approximate: the walk-back in
    /// <see cref="SummarizedConversation.FindIndexOfFirstMessageToKeep"/> may keep fewer to avoid
    /// opening the window inside a tool exchange.
    /// </summary>
    private readonly int targetCount;

    /// <summary>
    /// Hysteresis buffer above <see cref="targetCount"/>. Reduction triggers past their sum, not at the
    /// target, so a conversation sitting near the boundary does not pay a summarization call every turn.
    /// </summary>
    private readonly int thresholdCount;

    /// <summary>
    /// Logger for the one line reporting a reduction: it is otherwise invisible, since the reduced view
    /// never reaches storage and the agent is told nothing about it.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>Prompt driving the summarization call. Morgana always configures one, see <see cref="HistoryReducerService"/>.</summary>
    public string SummarizationPrompt { get; set; } =
        "Summarize the conversation so far, preserving every fact a later turn would need. " +
        "Report only what happened, without critique or interpretation.";

    /// <summary>
    /// Initializes a reducer that summarizes once the history grows past <paramref name="targetCount"/>
    /// plus <paramref name="thresholdCount"/> messages, leaving roughly <paramref name="targetCount"/> behind.
    /// </summary>
    /// <param name="chatClient">Client used for the summarization call — the calling agent's own tier.</param>
    /// <param name="targetCount">Messages to keep unsummarized. Must be greater than 0.</param>
    /// <param name="thresholdCount">Hysteresis buffer above the target before reduction triggers. Must be 0 or more.</param>
    /// <param name="logger">Logger for reduction diagnostics.</param>
    public MorganaChatReducer(IChatClient chatClient, int targetCount, int thresholdCount, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(targetCount, 0);
        ArgumentOutOfRangeException.ThrowIfNegative(thresholdCount);

        this.chatClient = chatClient;
        this.targetCount = targetCount;
        this.thresholdCount = thresholdCount;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<IEnumerable<ChatMessage>> ReduceAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        // Rebuilt from scratch on every turn: the reduced view is never stored, so the running summary
        // has to be recovered from the marker each time rather than carried in a field.
        SummarizedConversation conversation = SummarizedConversation.FromChatMessages(messages);
        int indexOfFirstMessageToKeep = conversation.FindIndexOfFirstMessageToKeep(targetCount, thresholdCount);

        // Zero means the history is still short enough: hand it back untouched and, above all, spend no
        // LLM call. This is the path almost every turn takes.
        if (indexOfFirstMessageToKeep <= 0)
            return conversation.ToChatMessages();

        logger.LogInformation(
            "MorganaChatReducer summarizing the first {SummarizedCount} unsummarized message(s), keeping {KeptCount}",
            indexOfFirstMessageToKeep, conversation.UnsummarizedCount - indexOfFirstMessageToKeep);

        // Reassigned rather than mutated — the struct is readonly, and folding produces a different
        // conversation: same tail, head replaced by its summary. This await sits in the turn's critical
        // path, so the user waits out a whole extra LLM round trip on the turn a reduction lands.
        conversation = await conversation.ResummarizeAsync(
            chatClient, indexOfFirstMessageToKeep, SummarizationPrompt, cancellationToken);

        return conversation.ToChatMessages();
    }

    /// <summary>
    /// A conversation split into running summary, leading system message and messages not yet summarized.
    /// Ported from <c>SummarizingChatReducer.SummarizedConversation</c>; only
    /// <see cref="ToSummarizerChatMessages"/> departs from it.
    /// </summary>
    /// <param name="summary">Summary carried over from an earlier reduction, or null on the first one. Prepended to the next summarization call so nothing is summarized twice from scratch.</param>
    /// <param name="systemMessage">The conversation's leading system message, held aside to be re-emitted ahead of the summary.</param>
    /// <param name="unsummarizedMessages">Messages not yet folded into <paramref name="summary"/>, in order. Everything the cut and the summarization operate on.</param>
    private readonly struct SummarizedConversation(string? summary, ChatMessage? systemMessage, List<ChatMessage> unsummarizedMessages)
    {
        /// <summary>Number of messages not yet folded into the summary.</summary>
        internal int UnsummarizedCount => unsummarizedMessages.Count;

        /// <summary>
        /// Splits a message list into its parts. The last message carrying <see cref="SummaryKey"/> wins and
        /// everything before it is dropped, since it already stands for those messages. Only the first system
        /// message survives, as upstream does.
        /// </summary>
        internal static SummarizedConversation FromChatMessages(IEnumerable<ChatMessage> messages)
        {
            string? summary = null;
            ChatMessage? systemMessage = null;
            List<ChatMessage> unsummarized = [];

            foreach (ChatMessage message in messages)
            {
                // Only the first system message is kept; any later one is dropped rather than reordered,
                // because a system message re-emitted after the summary would outrank it.
                if (message.Role == ChatRole.System)
                {
                    systemMessage ??= message;
                    continue;
                }

                if (message.AdditionalProperties?.TryGetValue(SummaryKey, out string? storedSummary) == true)
                {
                    // Everything accumulated so far is already covered by this summary, so it goes: what
                    // survives a fold is the summary, not the messages behind it. Later markers overwrite
                    // earlier ones, which is how a chain of reductions collapses to the most recent.
                    unsummarized.Clear();
                    summary = storedSummary;
                }
                else
                {
                    unsummarized.Add(message);
                }
            }

            return new SummarizedConversation(summary, systemMessage, unsummarized);
        }

        /// <summary>
        /// Index of the first message to keep out of the summary, or 0 while the history is short enough
        /// to leave alone.
        /// </summary>
        /// <remarks>
        /// The walk-back is the load-bearing rule: it pushes the cut earlier while the message before it is
        /// tool-related, so the kept window never opens inside a tool exchange and strands a result whose
        /// call was summarized away. It summarizes more than asked; erring the other way corrupts the
        /// history. The final scan then prefers a user-role boundary.
        /// </remarks>
        internal int FindIndexOfFirstMessageToKeep(int targetCount, int thresholdCount)
        {
            // The hysteresis gate, and also the floor for the search below: no cut may fall earlier than
            // this, or a reduction would summarize away more than the buffer was meant to protect.
            // Non-positive means the history has not yet outgrown target + threshold — nothing to do.
            int earliestAllowedIndex = unsummarizedMessages.Count - thresholdCount - targetCount;
            if (earliestAllowedIndex <= 0)
                return 0;

            // Where the cut would fall if only the target mattered.
            int cutIndex = unsummarizedMessages.Count - targetCount;

            // Walk it back while the message *before* the cut is tool-related. Cutting there would leave a
            // function result inside the kept window while its call went into the summary — an orphan the
            // provider rejects. Note this only ever moves the cut earlier, summarizing more than asked:
            // that direction is safe, the opposite one corrupts the history.
            while (cutIndex > 0 && unsummarizedMessages[cutIndex - 1].Contents.Any(IsToolRelatedContent))
                cutIndex--;

            // With the cut now safe, slide it further back onto a user turn if one is within reach, so the
            // kept window opens where the user spoke rather than mid-exchange. Bounded by the floor above.
            for (int candidate = cutIndex; candidate >= earliestAllowedIndex; candidate--)
                if (unsummarizedMessages[candidate].Role == ChatRole.User)
                    return candidate;

            // No user turn in range: the tool-safe cut stands.
            return cutIndex;
        }

        /// <summary>
        /// Runs the summarization call and returns the conversation with the summarized head replaced by its summary.
        /// </summary>
        /// <remarks>
        /// Stamping the summary mutates the caller's own <see cref="ChatMessage"/>, which is how it reaches
        /// the persisted session. Upstream behaves the same way.
        /// </remarks>
        internal async ValueTask<SummarizedConversation> ResummarizeAsync(
            IChatClient chatClient, int indexOfFirstMessageToKeep, string summarizationPrompt, CancellationToken cancellationToken)
        {
            // The one live call this class makes, paid once per reduction rather than per turn.
            IEnumerable<ChatMessage> summarizerMessages = ToSummarizerChatMessages(indexOfFirstMessageToKeep, summarizationPrompt);
            string newSummary = (await chatClient.GetResponseAsync(summarizerMessages, null, cancellationToken)).Text;

            // The anchor is the LAST message being summarized, not the first one kept: FromChatMessages
            // discards everything up to and including the marker, so parking it here is what makes the
            // summary stand in for exactly the messages behind it. The message belongs to the caller's
            // history, so this write is also how the summary reaches the persisted session.
            ChatMessage anchor = unsummarizedMessages[indexOfFirstMessageToKeep - 1];
            anchor.AdditionalProperties ??= [];
            anchor.AdditionalProperties[SummaryKey] = newSummary;

            return new SummarizedConversation(newSummary, systemMessage, [.. unsummarizedMessages.Skip(indexOfFirstMessageToKeep)]);
        }

        /// <summary>
        /// Rebuilds the reduced view for the agent: system message, summary as an assistant turn, then the
        /// kept messages — originals, never the projections used for summarizing.
        /// </summary>
        internal IEnumerable<ChatMessage> ToChatMessages()
        {
            if (systemMessage != null)
                yield return systemMessage;

            if (summary != null)
                yield return new ChatMessage(ChatRole.Assistant, summary);

            foreach (ChatMessage message in unsummarizedMessages)
                yield return message;
        }

        /// <summary>
        /// Content whose meaning depends on a counterpart elsewhere in the conversation. The set matches
        /// MEAI's exactly: it decides where the cut may fall, so changing it moves the safety property in
        /// <see cref="FindIndexOfFirstMessageToKeep"/>, not just the rendering.
        /// </summary>
        private static bool IsToolRelatedContent(AIContent content)
            => content is FunctionCallContent or FunctionResultContent or InputRequestContent or InputResponseContent;

        /// <summary>
        /// Builds the transcript sent to the summarizing model.
        /// </summary>
        /// <remarks>
        /// The one departure from upstream: a tool-related message is rendered as text instead of skipped.
        /// Rendered messages take the assistant role whatever their original one, because no tool protocol
        /// may survive into this call — a <c>Tool</c>-role message without its originating call is invalid.
        /// </remarks>
        private IEnumerable<ChatMessage> ToSummarizerChatMessages(int indexOfFirstMessageToKeep, string summarizationPrompt)
        {
            // The previous summary opens the transcript, so a fold builds on the last one instead of
            // re-deriving history the model can no longer see.
            if (summary != null)
                yield return new ChatMessage(ChatRole.Assistant, summary);

            // Strictly the messages being summarized: the kept window is not part of this call.
            for (int index = 0; index < indexOfFirstMessageToKeep; index++)
            {
                ChatMessage message = unsummarizedMessages[index];

                // Plain prose travels as-is, real message and real role.
                if (!message.Contents.Any(IsToolRelatedContent))
                {
                    yield return message;
                    continue;
                }

                // Anything tool-bearing becomes text. Upstream drops these, which is the whole defect;
                // rendering can still come back empty (a message carrying only content this doesn't know
                // how to write), and an empty turn is worth nothing to the summarizer.
                string rendered = RenderToolRelatedMessage(message);
                if (!string.IsNullOrWhiteSpace(rendered))
                    yield return new ChatMessage(ChatRole.Assistant, rendered);
            }

            // Last, not first: this is the instruction about the transcript above, and it reads as one.
            yield return new ChatMessage(ChatRole.System, summarizationPrompt);
        }

        /// <summary>
        /// Renders a tool-related message as text: its own prose, plus a line per call and per result.
        /// Values go in as they are — the summarizer can only quote what it was shown.
        /// </summary>
        private static string RenderToolRelatedMessage(ChatMessage message)
        {
            StringBuilder rendered = new StringBuilder();

            // Content order is preserved, so the rendering reads in the order the model produced it:
            // prose before a call, the call, then what came back.
            foreach (AIContent content in message.Contents)
            {
                switch (content)
                {
                    // A tool-bearing message can still carry prose of its own, and dropping it here would
                    // reintroduce the very hole this class exists to close.
                    case TextContent { Text.Length: > 0 } text:
                        rendered.AppendLine(text.Text);
                        break;

                    // The bracketed labels are the handle the summarization prompt reaches for when it
                    // tells the model these lines are the record of what happened — renaming them here
                    // means renaming them there too.
                    case FunctionCallContent call:
                        rendered.AppendLine($"[tool call] {call.Name}({Render(call.Arguments)})");
                        break;

                    case FunctionResultContent result:
                        rendered.AppendLine($"[tool result] {Render(result.Result)}");
                        break;

                    // Named but not unpacked: these only have to be visible enough that the summarizer
                    // does not read their message as empty.
                    case InputRequestContent:
                    case InputResponseContent:
                        rendered.AppendLine($"[{content.GetType().Name}]");
                        break;

                    // Everything else is deliberately skipped rather than guessed at.
                }
            }

            return rendered.ToString().TrimEnd();
        }

        /// <summary>
        /// Serializes a tool argument bag or return value to compact JSON, truncated to
        /// <see cref="MaxRenderedContentLength"/>. Never throws: an unserializable value is named by its type
        /// rather than failing a reduction the agent is waiting on.
        /// </summary>
        private static string Render(object? value)
        {
            if (value is null)
                return "null";

            string serialized;
            try
            {
                // A string is passed through rather than serialized, which would only wrap it in quotes
                // and escape its contents — noise the summarizer would have to read past.
                serialized = value as string ?? JsonSerializer.Serialize(value);
            }
            catch (Exception)
            {
                // Caught broadly and on purpose: this runs inside a reduction the agent's turn is waiting
                // on, and one unserializable argument is not worth failing that turn over.
                return $"<unserializable {value.GetType().Name}>";
            }

            // Truncation is marked so the model can tell a cut value from a complete one, and never
            // presents half an identifier as whole.
            return serialized.Length <= MaxRenderedContentLength
                ? serialized
                : $"{serialized[..MaxRenderedContentLength]}… (truncated)";
        }
    }
}
