using System.Text;
using System.Text.Json;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Adapters;

/// <summary>
/// Adapts fully-featured ChannelMessage to target channel capabilities. Three-path strategy: short-circuit hot path
/// if message fits; LLM rewrite via ChannelDowngrade prompt for semantic plain rendering; template fallback (markdown
/// strip, inline quick replies, drop rich cards) if LLM fails. Never throws; best-effort with semantic fidelity.
/// </summary>
public class MorganaChannelAdapter
{
    /// <summary>
    /// LLM service used to rewrite rich messages into a channel-compliant plain form when
    /// the target channel cannot carry the original features (rich cards, quick replies,
    /// markdown). Invoked only when <see cref="FitsWithin"/> rejects the message.
    /// </summary>
    private readonly ILLMService llmService;

    /// <summary>
    /// Resolves the ChannelDowngrade prompt that instructs the LLM on how to produce a
    /// semantically-equivalent plain rendering of a rich message, given the budget of
    /// capabilities advertised by the target channel.
    /// </summary>
    private readonly IPromptResolverService promptResolverService;

    /// <summary>
    /// Logger for diagnostic output. Emits informational entries when a message is
    /// degraded (with the triggering capability gap) and error entries when the LLM
    /// rewrite fails and the template fallback takes over.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Initialises a new instance of <see cref="MorganaChannelAdapter"/>.
    /// </summary>
    /// <param name="llmService">LLM service used to rewrite rich messages into channel-compliant plain form.</param>
    /// <param name="promptResolverService">Prompt resolver used to load the ChannelDowngrade prompt.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public MorganaChannelAdapter(
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger logger)
    {
        // The adapter is stateless between messages: it holds only the services it rewrites with.
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        this.logger = logger;
    }

    /// <summary>
    /// Adapts a message to channel capabilities: returns unchanged if it fits,
    /// otherwise rewrites via LLM or template fallback. Never null, never throws.
    /// </summary>
    /// <param name="channelMessage">Fully-featured outbound message</param>
    /// <param name="channelCapabilities">Expressive budget of the target channel</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Channel-conformant message</returns>
    public async Task<ChannelMessage> AdaptAsync(
        ChannelMessage channelMessage,
        ChannelCapabilities channelCapabilities,
        CancellationToken cancellationToken = default)
    {
        // A message that the channel can carry as it is costs neither a rewrite nor a model call.
        if (FitsWithin(channelMessage, channelCapabilities))
            return channelMessage;

        logger.LogInformation(
            "MorganaChannelAdapter: degrading message for conversation {ConversationId} " +
            "(hasRichCard={HasRichCard}, hasQuickReplies={HasQuickReplies}, " +
            "channelCaps=[richCards={SupportsRichCards}, quickReplies={SupportsQuickReplies}, " +
            "markdown={SupportsMarkdown}, maxLen={MaxLen}])",
            channelMessage.ConversationId,
            channelMessage.RichCard != null,
            channelMessage.QuickReplies is { Count: > 0 },
            channelCapabilities.SupportsRichCards,
            channelCapabilities.SupportsQuickReplies,
            channelCapabilities.SupportsMarkdown,
            channelCapabilities.MaxMessageLength);

        // The model rewrites the message into prose that keeps its meaning within the channel's budget.
        // Any failure falls through to the template below, because a message must always reach the user.
        try
        {
            // How a message is rewritten for a poorer channel is authored in morgana.json, under its own prompt.
            Records.Prompt adapterPrompt = await promptResolverService.ResolveAsync(Constants.Prompts.ChannelAdapter);

            // The model reads the channel's budget as data, in the placeholder the prompt names.
            string capabilitiesJson = JsonSerializer.Serialize(
                channelCapabilities, Records.DefaultJsonSerializerOptions);

            // The prompt is composed with the same labelled sections as every other framework prompt.
            string systemPrompt = string.Join("\n\n",
                    Records.Prompt.Labeled(Constants.SectionLabels.Target, adapterPrompt.Target),
                    Records.Prompt.Labeled(Constants.SectionLabels.Instructions, adapterPrompt.Instructions),
                    Records.Prompt.Labeled(Constants.SectionLabels.Formatting, adapterPrompt.Formatting))
                .Replace(Constants.Placeholders.ChannelCapabilities, capabilitiesJson);

            // The whole message travels as the model's input, so that the rewrite sees the card and the buttons it must absorb.
            string userPrompt = JsonSerializer.Serialize(channelMessage, Records.DefaultJsonSerializerOptions);

            // A single stateless call on the framework's tier, charged to the conversation whose message it adapts.
            string llmResponse = await llmService.CompleteWithSystemPromptAsync(
                channelMessage.ConversationId, systemPrompt, userPrompt);

            // An answer that is not the expected JSON is treated as no answer.
            Records.ChannelAdapterResponse? channelAdapterResponse =
                JsonSerializer.Deserialize<Records.ChannelAdapterResponse>(llmResponse, Records.DefaultJsonSerializerOptions);

            // Only a rewrite with text to read is delivered: buttons alone would leave the user without the answer.
            if (channelAdapterResponse != null && !string.IsNullOrWhiteSpace(channelAdapterResponse.Text))
            {
                // The LLM prompt instructs it to respect maxMessageLength, but we cannot trust it:
                // a rewrite that overshoots the hard limit would fail downstream on length-capped
                // channels (SMS, IVR, …). Apply the budget enforcement locally so the adapter
                // contract holds regardless of how disciplined the model was.
                string enforcedText = EnforceLengthBudget(channelAdapterResponse.Text, channelCapabilities);

                logger.LogInformation(
                    "MorganaChannelAdapter: LLM rewrite succeeded for {ConversationId} " +
                    "(rewrittenLength={Length}, enforcedLength={EnforcedLength}, rewrittenQuickReplies={QuickReplyCount})",
                    channelMessage.ConversationId,
                    channelAdapterResponse.Text.Length,
                    enforcedText.Length,
                    channelAdapterResponse.QuickReplies?.Count ?? 0);

                // The model may have folded the buttons into its text, so its own list wins where the channel keeps buttons.
                return Degrade(
                    channelMessage,
                    enforcedText,
                    channelCapabilities.SupportsQuickReplies
                        ? (channelAdapterResponse.QuickReplies ?? channelMessage.QuickReplies)
                        : null,
                    channelCapabilities);
            }

            // An empty rewrite is as useless as a failed one, so the template takes over.
            logger.LogWarning(
                "MorganaChannelAdapter: LLM returned empty or unparseable rewrite for {ConversationId} — using template fallback",
                channelMessage.ConversationId);
        }
        catch (Exception ex)
        {
            // Absorbed on purpose: the adapter never throws, since a failed adaptation must not lose the answer.
            logger.LogError(ex,
                "MorganaChannelAdapter: LLM rewrite failed for {ConversationId} — using template fallback", channelMessage.ConversationId);
        }

        // The deterministic rendering that needs no model.
        return BuildTemplateFallback(channelMessage, channelCapabilities);
    }

    // A message fits when the channel carries every feature it uses and its visual cost is within the length budget.
    private static bool FitsWithin(ChannelMessage channelMessage, ChannelCapabilities channelCapabilities)
        => (channelMessage.RichCard == null || channelCapabilities.SupportsRichCards)
           && (channelMessage.QuickReplies is not { Count: > 0 } || channelCapabilities.SupportsQuickReplies)
           && (channelCapabilities.SupportsMarkdown || !ContainsMarkdown(channelMessage.Text))
           && (channelCapabilities.MaxMessageLength is not { } max || EstimateVisualCost(channelMessage) <= max);

    // Sums visual cost of all message components (text, rich card, quick replies).
    private static int EstimateVisualCost(ChannelMessage channelMessage) =>
        channelMessage.Text.Length
      + (channelMessage.RichCard?.EstimateCost() ?? 0)
      + (channelMessage.QuickReplies?.Sum(quickReply => quickReply.EstimateCost()) ?? 0);

    // Detects markdown via Markdig parser: plain text is paragraphs of literals, whose lines may
    // simply follow one another. Only a hard break (trailing spaces, backslash) is markdown syntax.
    private static bool ContainsMarkdown(string text)
    {
        // Anything but paragraphs of literals and soft line breaks is syntax that a plain channel would show raw.
        MarkdownDocument document = Markdown.Parse(text);
        return document.Descendants()
                       .Any(node => node is not ParagraphBlock
                                    && node is not LiteralInline
                                    && node is not LineBreakInline { IsHard: false });
    }

    // Enforces MaxMessageLength: strip markdown first (cheaper); truncate with ellipsis if still over.
    private static string EnforceLengthBudget(string text, ChannelCapabilities channelCapabilities)
    {
        // A channel without a limit or a text within it needs no change.
        if (channelCapabilities.MaxMessageLength is not { } max || max <= 0 || text.Length <= max)
            return text;

        // Dropping the markup shortens the text without losing words, so it comes before cutting.
        string plain = StripMarkdown(text);
        if (plain.Length <= max)
            return plain;

        // Cut at the limit with an ellipsis that tells the reader the text goes on.
        return plain[..Math.Max(0, max - 1)] + "…";
    }

    // Renders the message for the channel with fixed rules, for when the model rewrite is unavailable.
    private static ChannelMessage BuildTemplateFallback(
        ChannelMessage channelMessage,
        ChannelCapabilities channelCapabilities)
    {
        // A rich card is dropped when the channel cannot carry it, never transcoded: only the model rewrite
        // can turn a card into prose while a title without its components would read as orphaned metadata.
        StringBuilder sb = new StringBuilder();
        sb.Append(channelMessage.Text);

        // A channel with no buttons receives the options as a plain "Options: A / B / C" line.
        if (channelMessage.QuickReplies is { Count: > 0 } && !channelCapabilities.SupportsQuickReplies)
        {
            // The options line is separated from the text only when there is text to separate it from.
            if (sb.Length > 0)
                sb.AppendLine().AppendLine();
            sb.Append("Options: ");
            sb.Append(string.Join(" / ", channelMessage.QuickReplies.Select(r => r.Label)));
        }

        string text = sb.ToString();

        // Markup is shown raw on a channel that cannot render it.
        if (!channelCapabilities.SupportsMarkdown)
            text = StripMarkdown(text);

        // The template is held to the same hard limit as the model's rewrite.
        text = EnforceLengthBudget(text, channelCapabilities);

        // Nothing rewrote the buttons, so the original ones go wherever the channel still carries them.
        return Degrade(channelMessage, text, channelMessage.QuickReplies, channelCapabilities);
    }

    // Copies the message with the degraded text, keeping the buttons and the card only where the channel carries them.
    private static ChannelMessage Degrade(
        ChannelMessage channelMessage,
        string text,
        List<QuickReply>? quickReplies,
        ChannelCapabilities channelCapabilities)
        => new ChannelMessage
        {
            ConversationId = channelMessage.ConversationId,
            Text = text,
            Timestamp = channelMessage.Timestamp,
            MessageType = channelMessage.MessageType,
            QuickReplies = channelCapabilities.SupportsQuickReplies ? quickReplies : null,
            RichCard = channelCapabilities.SupportsRichCards ? channelMessage.RichCard : null,
            ErrorReason = channelMessage.ErrorReason,
            AgentName = channelMessage.AgentName,
            AgentCompleted = channelMessage.AgentCompleted,
            FadingMessageDurationSeconds = channelMessage.FadingMessageDurationSeconds,
            ConversationMetadata = channelMessage.ConversationMetadata,

            // A command's frame stays one when its text is degraded: its finished frame is how a channel
            // matches the outcome to the command it is waiting on
            Progress = channelMessage.Progress
        };

    // The text as a channel without markdown shows it: the words of every block, with the markup gone.
    private static string StripMarkdown(string text)
    {
        // The whole document is rendered block by block into the plain text the channel receives.
        StringBuilder sb = new StringBuilder();
        RenderContainerBlock(Markdown.Parse(text), sb);

        // Every block ends with a blank line, so the last one is trimmed off.
        return sb.ToString().TrimEnd();
    }

    // Renders each block of a document, list or quote as plain text, a blank line apart from the next.
    private static void RenderContainerBlock(ContainerBlock containerBlock, StringBuilder sb)
    {
        foreach (Block block in containerBlock)
        {
            // A block with no words to keep, such as a thematic break or raw HTML, leaves nothing behind.
            switch (block)
            {
                // A paragraph keeps its words and stands as a block of its own.
                case ParagraphBlock paragraph:
                    RenderContainerInline(paragraph.Inline, sb);
                    sb.AppendLine().AppendLine();
                    break;

                // A heading loses its marker and reads as a paragraph.
                case HeadingBlock heading:
                    RenderContainerInline(heading.Inline, sb);
                    sb.AppendLine().AppendLine();
                    break;

                // Code keeps every line as written, since its layout is part of what it says.
                case CodeBlock code:
                    foreach (var line in code.Lines.Lines)
                        sb.AppendLine(line.ToString());
                    sb.AppendLine();
                    break;

                // A list or a quote loses its markers and its content is rendered where it stands.
                case ContainerBlock nested:
                    RenderContainerBlock(nested, sb);
                    break;
            }
        }
    }

    // Renders the inline content of a paragraph, a heading or a link as plain text.
    private static void RenderContainerInline(ContainerInline? containerInline, StringBuilder sb)
    {
        // A paragraph or heading may have no inline content.
        if (containerInline == null)
            return;

        // Each inline of the paragraph is read in turn, so the plain text keeps the order it was written in.
        foreach (Inline inline in containerInline)
        {
            // An inline with nothing to read, such as raw HTML, leaves nothing behind.
            switch (inline)
            {
                // Plain words go through as written.
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;

                // Inline code keeps its content and loses its backticks.
                case CodeInline code:
                    sb.Append(code.Content);
                    break;

                // A line break of the source, soft or hard, stays a line break for the reader.
                case LineBreakInline:
                    sb.AppendLine();
                    break;

                // A bare address is all an autolink has to say, so the address is what the reader gets.
                case AutolinkInline autolink:
                    sb.Append(autolink.Url);
                    break;

                // A link keeps its label: the address behind it cannot be followed from plain text.
                case LinkInline link:
                    RenderContainerInline(link, sb);
                    break;

                // Emphasis loses its markers and keeps its words.
                case ContainerInline nested:
                    RenderContainerInline(nested, sb);
                    break;
            }
        }
    }
}