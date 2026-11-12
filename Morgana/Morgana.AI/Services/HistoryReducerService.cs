using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Extensions;

namespace Morgana.AI.Services;

// IChatReducer is marked experimental (MEAI001), yet it is the abstraction Microsoft.Agents.AI reduces a history through.
#pragma warning disable MEAI001 // Type is for evaluation purposes only and is subject to change or removal in future updates

/// <summary>
/// Builds the reducer that keeps an agent's history from outgrowing the model reading it, one per agent on that agent's own tier.
/// Reduction folds the oldest stretch into a summary and leaves the recent messages verbatim: the record keeps everything,
/// but what the summary drops the model never gets back.
/// </summary>
public class HistoryReducerService
{
    /// <summary>The configuration, read for <c>Morgana:HistoryReducer</c> each time an agent is built, so its reducer reflects the settings in force then.</summary>
    private readonly IConfiguration configuration;

    /// <summary>
    /// Logs each reducer's settings and the disabled case.
    /// A disabled reducer is a null, which would otherwise be indistinguishable from a misconfiguration.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>Binds the factory to the configuration it reads at every agent's creation.</summary>
    public HistoryReducerService(
        IConfiguration configuration,
        ILogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    /// <summary>
    /// Creates the reducer of one agent; null when reduction is disabled, which hands the model the whole history.
    /// The null is no failure: <c>MorganaChatHistoryProvider</c> reads it as the only way to turn reduction off.
    /// </summary>
    /// <param name="chatClient">The client the summarization call runs on, the agent's own tier.</param>
    public IChatReducer? CreateReducer(IChatClient chatClient)
    {
        // The reducer's settings, as configured when the agent is built.
        IConfigurationSection reducerSection = configuration.GetSection("Morgana:HistoryReducer");

        // Switched off, the agent reads its whole history every turn.
        if (!reducerSection.GetValue("Enabled", true))
        {
            logger.LogInformation("History summarization disabled - no reducer created");
            return null;
        }

        // Defaults mirror the shipped appsettings.json: first reduction at 21 messages.
        int targetCount = reducerSection.GetValue<int>("SummarizationTargetCount", 8);
        int threshold = reducerSection.GetValue<int>("SummarizationThreshold", 12);

        // Each agent gets its own reducer on its own tier, so summarizing a history costs the agent whose history grew.
        MorganaChatReducer chatReducer = new MorganaChatReducer(chatClient, targetCount, threshold, logger);

        // The reducer carries a built-in prompt, which a blank SummarizationPrompt leaves in place rather than emptying.
        string? summaryPrompt = reducerSection.GetValue<string?>("SummarizationPrompt");
        if (!string.IsNullOrWhiteSpace(summaryPrompt))
            chatReducer.SummarizationPrompt = summaryPrompt;

        logger.LogInformation(
            "Created MorganaChatReducer: target={TargetCount}, threshold(buffer)={Threshold} → reduction triggers when message count > {Trigger}",
            targetCount, threshold, targetCount + threshold);

        // The history provider reduces the agent's history through it on every turn.
        return chatReducer;
    }
}

/// <summary>
/// Summarizes an agent's history like MEAI's <c>SummarizingChatReducer</c>, but shows the summarizer the tool calls and results.
/// MEAI drops every tool-bearing message from the summarizer's input, so its summary reports that no tool ran and no identifier exists.
/// Which messages are summarized is a faithful port of MEAI's and the kept window is handed back untouched.
/// </summary>
public sealed class MorganaChatReducer : IChatReducer
{
    /// <summary>The property stamped on the last summarized message, under the name MEAI uses.</summary>
    public const string SummaryKey = Constants.MessageProperties.Summary;

    /// <summary>
    /// The rendered length of one content item at most, so a catalogue-sized tool return cannot dominate the call.
    /// Generous, since identifiers are short and the point is that they survive.
    /// </summary>
    private const int MaxRenderedContentLength = 4000;

    /// <summary>The client the summarization runs on, the agent's own tier, so its cost lands on the agent whose history grew.</summary>
    private readonly IChatClient chatClient;

    /// <summary>
    /// The messages left unsummarized behind the cut.
    /// Approximate: the cut may keep fewer so that the window never opens inside a tool exchange.
    /// </summary>
    private readonly int targetCount;

    /// <summary>
    /// The buffer above the target that a history must outgrow before it is reduced.
    /// Reducing past the sum rather than at the target spares a conversation near the boundary a summarization every turn.
    /// </summary>
    private readonly int thresholdCount;

    /// <summary>
    /// Logs the one line that reports a reduction.
    /// The reduced view never reaches storage and the agent is told nothing, so the reduction is otherwise invisible.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>The instruction of the summarization call, which the shipped configuration always replaces with its own.</summary>
    public string SummarizationPrompt { get; set; } =
        "Summarize the conversation so far, preserving every fact a later turn would need. " +
        "Report only what happened, without critique or interpretation.";

    /// <summary>Builds a reducer that summarizes once the history outgrows the target plus the buffer, leaving about the target behind.</summary>
    /// <param name="chatClient">The client the summarization call runs on, the agent's own tier.</param>
    /// <param name="targetCount">Messages to keep unsummarized. Must be greater than 0.</param>
    /// <param name="thresholdCount">Hysteresis buffer above the target before reduction triggers. Must be 0 or more.</param>
    /// <param name="logger">Logger for reduction diagnostics.</param>
    public MorganaChatReducer(IChatClient chatClient, int targetCount, int thresholdCount, ILogger logger)
    {
        // A reducer keeping no message would summarize away the turn being answered.
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
        // The history provider always hands a history, empty at worst.
        ArgumentNullException.ThrowIfNull(messages);

        // The reduced view is never stored, so the running summary is recovered from its marker on every turn.
        SummarizedConversation conversation = SummarizedConversation.FromChatMessages(messages);
        int indexOfFirstMessageToKeep = conversation.FindIndexOfFirstMessageToKeep(targetCount, thresholdCount);

        // Zero means the history is still short enough: hand it back untouched and, above all, spend no
        // LLM call. This is the path almost every turn takes.
        if (indexOfFirstMessageToKeep <= 0)
            return conversation.ToChatMessages();

        logger.LogInformation(
            "MorganaChatReducer summarizing the first {SummarizedCount} unsummarized message(s), keeping {KeptCount}",
            indexOfFirstMessageToKeep, conversation.UnsummarizedCount - indexOfFirstMessageToKeep);

        // The same tail with the head replaced by its summary. The user waits out a whole extra call to the model
        // on the turn that a reduction lands on.
        conversation = await conversation.ResummarizeAsync(
            chatClient, indexOfFirstMessageToKeep, SummarizationPrompt, cancellationToken);

        // What the agent's model reads this turn. The full history stays in the session untouched:
        // this view is rebuilt on every turn and never stored.
        return conversation.ToChatMessages();
    }

    /// <summary>
    /// Folds everything but the most recent target messages into a summary now, returning how many were folded.
    /// The summary is stamped on the caller's own message as an automatic reduction stamps it, so a session saved
    /// afterwards carries the fold while nothing is removed from the record.
    /// </summary>
    public async Task<int> CompactAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken)
    {
        // The command always hands the agent's history, empty at worst.
        ArgumentNullException.ThrowIfNull(messages);

        // An earlier fold leaves its own mark in the history, so what this one may fold is what has been
        // said since that mark, never the whole conversation again.
        SummarizedConversation conversation = SummarizedConversation.FromChatMessages(messages);

        // No buffer above the target here: the buffer exists to spare turns a reduction nobody asked for,
        // while this fold was asked for. So the cut falls wherever the kept window ends, tool exchanges
        // and the opening user turn respected exactly as they are on an automatic reduction.
        int indexOfFirstMessageToKeep = conversation.FindIndexOfFirstMessageToKeep(targetCount, thresholdCount: 0);

        // Everything said so far already fits the window a turn would be given: folding would spend a call
        // to summarize what the model was going to read whole anyway.
        if (indexOfFirstMessageToKeep <= 0)
            return 0;

        logger.LogInformation(
            "MorganaChatReducer folding {SummarizedCount} message(s) on demand, keeping {KeptCount}",
            indexOfFirstMessageToKeep, conversation.UnsummarizedCount - indexOfFirstMessageToKeep);

        // The summary lands on the message it stands behind, in the caller's own history. That mark is the
        // whole effect of this call, which a later reduction reads to know where its window opens.
        await conversation.ResummarizeAsync(chatClient, indexOfFirstMessageToKeep, SummarizationPrompt, cancellationToken);

        // What the summary now answers for, which is what the user is told was compacted.
        return indexOfFirstMessageToKeep;
    }

    /// <summary>
    /// A conversation split into its running summary, its leading system message and the messages not yet summarized.
    /// Ported from MEAI's <c>SummarizingChatReducer.SummarizedConversation</c>; only <see cref="ToSummarizerChatMessages"/> departs from it.
    /// </summary>
    /// <param name="summary">The summary of an earlier reduction, null on the first one, which opens the next summarization call.</param>
    /// <param name="systemMessage">The conversation's leading system message, held aside to be re-emitted ahead of the summary.</param>
    /// <param name="unsummarizedMessages">The messages not yet folded into the summary, in order, which the cut and the summarization work on.</param>
    private readonly struct SummarizedConversation(string? summary, ChatMessage? systemMessage, List<ChatMessage> unsummarizedMessages)
    {
        /// <summary>The number of messages not yet folded into the summary.</summary>
        internal int UnsummarizedCount => unsummarizedMessages.Count;

        /// <summary>
        /// Splits a history into its parts: the last message carrying <see cref="SummaryKey"/> stands for everything before it.
        /// Only the first system message survives, as MEAI keeps it.
        /// </summary>
        internal static SummarizedConversation FromChatMessages(IEnumerable<ChatMessage> messages)
        {
            // No summary and no system message until the walk finds them.
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

                // A message an earlier reduction stamped: it is the last one that reduction summarized.
                // What it carries stands for itself together with everything before it. This is why
                // the running summary travels inside the history rather than in a field of this class.
                // A marker read back from the record arrives as JSON and is still recognised, which lets a
                // session resumed or rewritten by /compact keep its summary.
                if (message.ReadTextProperty(SummaryKey) is string storedSummary)
                {
                    // Everything gathered so far is already covered by that summary, so it goes: what
                    // survives a fold is the summary, never the messages behind it. A later marker
                    // overwrites an earlier one, which collapses a chain of reductions to the most recent.
                    unsummarized.Clear();
                    summary = storedSummary;
                }

                // Everything after the last marker: the tail no reduction has folded yet, which is what
                // the cut below is measured against.
                else
                {
                    unsummarized.Add(message);
                }
            }

            // The three parts of the history.
            return new SummarizedConversation(summary, systemMessage, unsummarized);
        }

        /// <summary>Returns the index of the first message to keep out of the summary, 0 while the history is short enough to leave alone.</summary>
        internal int FindIndexOfFirstMessageToKeep(int targetCount, int thresholdCount)
        {
            // The hysteresis gate and also the floor for the search below: a cut earlier than this would
            // summarize away more than the buffer was meant to protect.
            // Zero or less means the history has not yet outgrown the target and the buffer together.
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
            {
                // The nearest user turn opens the kept window.
                if (unsummarizedMessages[candidate].Role == ChatRole.User)
                    return candidate;
            }

            // No user turn in range: the tool-safe cut stands.
            return cutIndex;
        }

        /// <summary>
        /// Runs the summarization call and returns the conversation with its summarized head replaced by the summary.
        /// The summary is stamped on the caller's own message, which is how it reaches the persisted session, as in MEAI.
        /// </summary>
        internal async ValueTask<SummarizedConversation> ResummarizeAsync(
            IChatClient chatClient, int indexOfFirstMessageToKeep, string summarizationPrompt, CancellationToken cancellationToken)
        {
            // The one live call this class makes, paid once per reduction rather than per turn.
            IEnumerable<ChatMessage> summarizerMessages = ToSummarizerChatMessages(indexOfFirstMessageToKeep, summarizationPrompt);

            // This answer stands in for every message behind the anchor for the rest of the conversation,
            // so what it leaves out is lost to the agent rather than merely shortened.
            string newSummary = (await chatClient.GetResponseAsync(summarizerMessages, null, cancellationToken)).Text;

            // The anchor is the last message being summarized, not the first one kept: FromChatMessages
            // discards everything up to and including the marker, so parking it here is what makes the
            // summary stand in for exactly the messages behind it. The message belongs to the caller's
            // history, so this write is also how the summary reaches the persisted session.
            ChatMessage anchor = unsummarizedMessages[indexOfFirstMessageToKeep - 1];
            anchor.AdditionalProperties ??= [];
            anchor.AdditionalProperties[SummaryKey] = newSummary;

            // The kept tail behind the new summary.
            return new SummarizedConversation(newSummary, systemMessage, [.. unsummarizedMessages.Skip(indexOfFirstMessageToKeep)]);
        }

        /// <summary>Rebuilds the reduced view for the agent: the system message, the summary as an assistant turn, then the kept messages as written.</summary>
        internal IEnumerable<ChatMessage> ToChatMessages()
        {
            // Ahead of everything, where the original history had it: re-emitted after the summary it
            // would outrank it.
            if (systemMessage != null)
                yield return systemMessage;

            // The summary enters as the model's own turn, not as an instruction: it is an account of what
            // happened. Nothing in it binds the agent the way a system message would.
            if (summary != null)
                yield return new ChatMessage(ChatRole.Assistant, summary);

            // The kept tail exactly as the caller wrote it, never the [tool call] projections: those
            // exist only to be read by the summarizing model.
            foreach (ChatMessage message in unsummarizedMessages)
                yield return message;
        }

        /// <summary>
        /// Whether a content's meaning depends on a counterpart elsewhere in the conversation, as MEAI's set of tool contents decides it.
        /// The set decides where the cut may fall, so changing it moves the safety of the cut and not just the rendering.
        /// </summary>
        private static bool IsToolRelatedContent(AIContent content)
            // A call, its result and an approval request with its answer each pair with another message.
            => content is FunctionCallContent or FunctionResultContent or InputRequestContent or InputResponseContent;

        /// <summary>
        /// Builds the transcript sent to the summarizing model, rendering a tool-bearing message as text where MEAI skips it.
        /// A rendered message takes the assistant role, because no tool protocol may survive into this call.
        /// </summary>
        private IEnumerable<ChatMessage> ToSummarizerChatMessages(int indexOfFirstMessageToKeep, string summarizationPrompt)
        {
            // The previous summary opens the transcript, so a fold builds on the last one instead of
            // re-deriving history the model can no longer see.
            if (summary != null)
                yield return new ChatMessage(ChatRole.Assistant, summary);

            // Strictly the messages being summarized: the kept window is not part of this call.
            for (int index = 0; index < indexOfFirstMessageToKeep; index++)
            {
                // The messages are read in the order they were said.
                ChatMessage message = unsummarizedMessages[index];

                // Plain prose travels as-is, real message and real role.
                if (!message.Contents.Any(IsToolRelatedContent))
                {
                    yield return message;
                    continue;
                }

                // Anything tool-bearing becomes text, where MEAI drops it. A rendering comes back empty for a message
                // carrying only content this does not write: an empty turn is worth nothing to the summarizer.
                string rendered = RenderToolRelatedMessage(message);
                if (!string.IsNullOrWhiteSpace(rendered))
                    yield return new ChatMessage(ChatRole.Assistant, rendered);
            }

            // Last, not first: this is the instruction about the transcript above and it reads as one.
            // It speaks as the user because the request must end on the user's side: a provider lifts
            // system messages out of the turns, which would leave the last summarized assistant message
            // closing the request. Gemini refuses a request that ends on the model's turn. The call is
            // never stored, so no record ever shows these words as the user's.
            yield return new ChatMessage(ChatRole.User, summarizationPrompt);
        }

        /// <summary>
        /// Renders a tool-bearing message as text: its own prose with one line per call and per result.
        /// Values go in as they are, since the summarizer can only quote what it was shown.
        /// </summary>
        private static string RenderToolRelatedMessage(ChatMessage message)
        {
            // One line per content item.
            StringBuilder rendered = new StringBuilder();

            // Content order is preserved, so the rendering reads in the order the model produced it:
            // prose before a call, the call, then what came back.
            foreach (AIContent content in message.Contents)
            {
                // The bracketed labels below are the handle the summarization prompt reaches for when it
                // tells the model these lines are the record of what happened. Renaming one here means
                // renaming it there too.
                switch (content)
                {
                    // A tool-bearing message can still carry prose of its own and dropping it here would
                    // reintroduce the very hole this class exists to close.
                    case TextContent { Text.Length: > 0 } text:
                        rendered.AppendLine(text.Text);
                        break;

                    // The call the agent made, arguments included. MEAI's own reducer drops the whole
                    // message here, which is why its summaries report that no tool ever ran.
                    case FunctionCallContent call:
                        rendered.AppendLine(CultureInfo.InvariantCulture, $"[tool call] {call.Name}({Render(call.Arguments)})");
                        break;

                    // What the tool answered, which is where identifiers are born: an order number the
                    // summarizer never sees is one the agent can no longer cite later in the conversation.
                    case FunctionResultContent result:
                        rendered.AppendLine(CultureInfo.InvariantCulture, $"[tool result] {Render(result.Result)}");
                        break;

                    // Named but not unpacked: these only have to be visible enough that the summarizer
                    // does not read their message as empty.
                    case InputRequestContent:
                    case InputResponseContent:
                        rendered.AppendLine(CultureInfo.InvariantCulture, $"[{content.GetType().Name}]");
                        break;

                    // Everything else is deliberately skipped rather than guessed at.
                }
            }

            // The rendering closes on its last line.
            return rendered.ToString().TrimEnd();
        }

        /// <summary>
        /// Serializes a tool's arguments or result to compact JSON, cut at <see cref="MaxRenderedContentLength"/>, never throwing.
        /// An unserializable value is named by its type rather than failing a reduction the agent is waiting on.
        /// </summary>
        private static string Render(object? value)
        {
            // A tool that returned nothing is shown as such.
            if (value is null)
                return "null";

            // The value as the summarizer reads it.
            string serialized;
            try
            {
                // A string is passed through rather than serialized, which would only wrap it in quotes
                // and escape its contents: noise the summarizer would have to read past.
                serialized = value as string ?? JsonSerializer.Serialize(value);
            }
            catch (Exception)
            {
                // Caught broadly and on purpose: this runs inside a reduction the agent's turn is waiting
                // on and one unserializable argument is not worth failing that turn over.
                return $"<unserializable {value.GetType().Name}>";
            }

            // Truncation is marked so the model can tell a cut value from a complete one and never
            // presents half an identifier as whole.
            return serialized.Length <= MaxRenderedContentLength
                ? serialized
                : $"{serialized[..MaxRenderedContentLength]}… (truncated)";
        }
    }
}