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
/// Fits an outbound message to what the target channel can show, never throwing.
/// A message that fits passes untouched, one that does not is rewritten by the ChannelAdapter prompt
/// and a failed rewrite falls back to a template that strips markdown, inlines the buttons and drops the card.
/// </summary>
public class MorganaChannelAdapter
{
    /// <summary>Runs the rewrite on the framework tier, charged to the conversation; asked only for a message that does not fit.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>Resolves the ChannelAdapter prompt and Morgana's own, whose personality the rewrite keeps.</summary>
    private readonly IPromptResolverService promptResolverService;

    /// <summary>Records each degradation with the capabilities that caused it and each rewrite that fell back to the template.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the adapter to the rewriting model and the prompts it reads.</summary>
    /// <param name="llmService">Runs the rewrite.</param>
    /// <param name="promptResolverService">Resolves the ChannelAdapter prompt and Morgana's own.</param>
    /// <param name="logger">Records degradations and fallbacks.</param>
    public MorganaChannelAdapter(
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        ILogger logger)
    {
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        this.logger = logger;
    }

    /// <summary>Returns the message as the channel can show it: untouched when it fits, rewritten otherwise; never throws.</summary>
    /// <param name="channelMessage">The outbound message with every feature its agent gave it.</param>
    /// <param name="channelCapabilities">What the target channel can show.</param>
    /// <param name="cancellationToken">Not observed: the rewrite runs on the framework tier's own budget.</param>
    /// <returns>A message within the channel's capabilities.</returns>
    public async Task<ChannelMessage> AdaptAsync(
        ChannelMessage channelMessage,
        ChannelCapabilities channelCapabilities,
        CancellationToken cancellationToken = default)
    {
        // On a channel without buttons the user always writes in prose, where asking something else and taking
        // leave need no button: the escape pair is dropped rather than rewritten into the text. A message left
        // with only the pair then fits the channel and reaches it untouched.
        if (!channelCapabilities.SupportsQuickReplies && channelMessage.QuickReplies is { Count: > 0 })
        {
            // The agent's own buttons, which the adapter still has to carry into the text; none when the pair was all there was.
            List<QuickReply> domainQuickReplies =
            [
                .. channelMessage.QuickReplies.Where(quickReply => !string.Equals(quickReply.Id, Constants.EscapeQuickReplyIds.ContinueAgent, StringComparison.Ordinal)
                                                                     && !string.Equals(quickReply.Id, Constants.EscapeQuickReplyIds.ExitAgent, StringComparison.Ordinal))
            ];
            channelMessage = channelMessage with { QuickReplies = domainQuickReplies.Count > 0 ? domainQuickReplies : null };
        }

        // A message within every capability of the channel reaches it as its agent wrote it.
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

        try
        {
            // The rewrite's task, authored in morgana.json under the ChannelAdapter prompt.
            Records.Prompt adapterPrompt = await promptResolverService.ResolveAsync(Constants.Prompts.ChannelAdapter);

            // A rewritten message still reaches the user as Morgana's, so the rewrite keeps the voice her agents speak in.
            Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync(Constants.Morgana);

            // The capabilities as the channel announced them, for the placeholder that tells the model the limits it writes to.
            string capabilitiesJson = JsonSerializer.Serialize(
                channelCapabilities, Records.DefaultJsonSerializerOptions);

            // What the rewriting model reads: its task, Morgana's voice and the capabilities of the target channel.
            string systemPrompt = string.Join("\n\n",
                    Records.Prompt.Labeled(Constants.SectionLabels.Target, adapterPrompt.Target),
                    Records.Prompt.Labeled(Constants.SectionLabels.Personality, morganaPrompt.Personality),
                    Records.Prompt.Labeled(Constants.SectionLabels.Instructions, adapterPrompt.Instructions),
                    Records.Prompt.Labeled(Constants.SectionLabels.Formatting, adapterPrompt.Formatting))
                .Replace(Constants.Placeholders.ChannelCapabilities, capabilitiesJson);

            // The whole message as the agent wrote it, card and buttons included, so the rewrite can carry them into prose.
            string userPrompt = JsonSerializer.Serialize(channelMessage, Records.DefaultJsonSerializerOptions);

            // One call on the framework tier, charged to the conversation the message belongs to.
            string llmResponse = await llmService.CompleteWithSystemPromptAsync(
                channelMessage.ConversationId, systemPrompt, userPrompt);

            // The prompt asks for the text and the buttons it kept as JSON; anything else fails here and falls to the template.
            Records.ChannelAdapterResponse? channelAdapterResponse =
                JsonSerializer.Deserialize<Records.ChannelAdapterResponse>(llmResponse, Records.DefaultJsonSerializerOptions);

            // Only a rewrite with some text replaces the message; anything else falls to the template.
            if (channelAdapterResponse != null && !string.IsNullOrWhiteSpace(channelAdapterResponse.Text))
            {
                // The prompt asks for the channel's length but a model may overshoot it, which a length-capped
                // channel such as SMS would refuse: the limit is enforced here whatever the model wrote.
                string enforcedText = EnforceLengthBudget(channelAdapterResponse.Text, channelCapabilities);

                logger.LogInformation(
                    "MorganaChannelAdapter: LLM rewrite succeeded for {ConversationId} " +
                    "(rewrittenLength={Length}, enforcedLength={EnforcedLength}, rewrittenQuickReplies={QuickReplyCount})",
                    channelMessage.ConversationId,
                    channelAdapterResponse.Text.Length,
                    enforcedText.Length,
                    channelAdapterResponse.QuickReplies?.Count ?? 0);

                // The rewrite replaces the text and the buttons the channel can show: everything else travels as it came
                return channelMessage with
                {
                    Text = enforcedText,
                    QuickReplies = channelCapabilities.SupportsQuickReplies
                        ? (channelAdapterResponse.QuickReplies ?? channelMessage.QuickReplies)
                        : null,
                    RichCard = channelCapabilities.SupportsRichCards ? channelMessage.RichCard : null
                };
            }

            logger.LogWarning(
                "MorganaChannelAdapter: LLM returned empty or unparseable rewrite for {ConversationId} — using template fallback",
                channelMessage.ConversationId);
        }
        catch (Exception ex)
        {
            // A rewrite that fails costs the message its rewrite, never its delivery.
            logger.LogError(ex,
                "MorganaChannelAdapter: LLM rewrite failed for {ConversationId} — using template fallback", channelMessage.ConversationId);
        }

        // The template needs no model, so the message always reaches the channel in some form.
        return BuildTemplateFallback(channelMessage, channelCapabilities);
    }

    /// <summary>Whether the message uses only what the channel can show and fits its length.</summary>
    private static bool FitsWithin(ChannelMessage channelMessage, ChannelCapabilities channelCapabilities)
    {
        // A card on a channel that draws none would be lost, so the rewrite has to carry it into prose.
        if (channelMessage.RichCard != null && !channelCapabilities.SupportsRichCards)
            return false;

        // Buttons on a channel that draws none would leave the user without the choice they offer.
        if (channelMessage.QuickReplies is { Count: > 0 } && !channelCapabilities.SupportsQuickReplies)
            return false;

        // Markdown on a channel without it would show its asterisks and hashes as text.
        if (!channelCapabilities.SupportsMarkdown && ContainsMarkdown(channelMessage.Text))
            return false;

        // A length-capped channel such as SMS refuses what runs past its limit, card and buttons counted.
        if (channelCapabilities.MaxMessageLength is { } maxMessageLength && EstimateVisualCost(channelMessage) > maxMessageLength)
            return false;

        // Within every capability, the message reaches the channel as its agent wrote it.
        return true;
    }

    /// <summary>The length the message takes on screen: its text plus the cost its card and buttons estimate for themselves.</summary>
    private static int EstimateVisualCost(ChannelMessage channelMessage) =>
        channelMessage.Text.Length
      + (channelMessage.RichCard?.EstimateCost() ?? 0)
      + (channelMessage.QuickReplies?.Sum(quickReply => quickReply.EstimateCost()) ?? 0);

    /// <summary>
    /// Whether the text uses any markdown syntax.
    /// Plain text parses as paragraphs of literals whose lines may simply follow one another: only a hard break is syntax.
    /// </summary>
    private static bool ContainsMarkdown(string text)
    {
        // Any node other than a paragraph, a literal or a soft line break is markdown the channel would show raw.
        MarkdownDocument document = Markdown.Parse(text);
        return document.Descendants()
                       .Any(node => node is not ParagraphBlock
                                    && node is not LiteralInline
                                    && node is not LineBreakInline { IsHard: false });
    }

    /// <summary>Cuts the text to the channel's length: without its markdown first, then with an ellipsis when that is still too long.</summary>
    private static string EnforceLengthBudget(string text, ChannelCapabilities channelCapabilities)
    {
        // A channel with no length limit keeps the text as it is, as does a text already within the limit.
        if (channelCapabilities.MaxMessageLength is not { } maxMessageLength || maxMessageLength <= 0 || text.Length <= maxMessageLength)
            return text;

        // Dropping the markdown syntax shortens the text without losing a word.
        string plain = StripMarkdown(text);
        if (plain.Length <= maxMessageLength)
            return plain;

        // Still too long: cut with an ellipsis that keeps the result within the limit.
        return plain[..Math.Max(0, maxMessageLength - 1)] + "…";
    }

    /// <summary>
    /// The message within the channel's capabilities without a model: the buttons inlined as text, the markdown stripped and the length cut.
    /// A card is dropped, since its title alone would look alien beside the text and only the model's rewrite can turn a card into prose.
    /// </summary>
    private static ChannelMessage BuildTemplateFallback(
        ChannelMessage channelMessage,
        ChannelCapabilities channelCapabilities)
    {
        // The agent's text comes first, untouched until the markdown and length rules below apply to the whole.
        StringBuilder sb = new StringBuilder();
        sb.Append(channelMessage.Text);

        // Buttons the channel cannot draw become one line of options under the text.
        if (channelMessage.QuickReplies is { Count: > 0 } && !channelCapabilities.SupportsQuickReplies)
        {
            // A blank line sets the options apart, so they do not read as the end of the agent's sentence.
            if (sb.Length > 0)
                sb.AppendLine().AppendLine();

            // "Options: A / B / C" stays scannable without any markdown.
            sb.Append("Options: ");
            sb.Append(string.Join(" / ", channelMessage.QuickReplies.Select(quickReply => quickReply.Label)));
        }

        string text = sb.ToString();

        // A channel that would show markdown syntax as text gets the plain words.
        if (!channelCapabilities.SupportsMarkdown)
            text = StripMarkdown(text);

        // The length limit applies to the whole fallback, the inlined options included.
        text = EnforceLengthBudget(text, channelCapabilities);

        // Only the text and the features the channel lacks change: a command's frame stays one when its text is
        // degraded, since its finished frame is how a channel matches the outcome to the command it is waiting on
        return channelMessage with
        {
            Text = text,
            QuickReplies = channelCapabilities.SupportsQuickReplies ? channelMessage.QuickReplies : null,
            RichCard = channelCapabilities.SupportsRichCards ? channelMessage.RichCard : null
        };
    }

    /// <summary>The words of a markdown text without its syntax, its blocks kept apart by blank lines.</summary>
    private static string StripMarkdown(string text)
    {
        // The parse tree yields the words; the trailing blank lines of the last block are dropped.
        StringBuilder sb = new StringBuilder();
        RenderContainerBlock(Markdown.Parse(text), sb);
        return sb.ToString().TrimEnd();
    }

    /// <summary>Writes the words of a block container, each block followed by a blank line.</summary>
    private static void RenderContainerBlock(ContainerBlock containerBlock, StringBuilder sb)
    {
        // A block of any other kind, such as a thematic break, carries no words and is left out.
        foreach (Block block in containerBlock)
        {
            switch (block)
            {
                // A paragraph's words, followed by the blank line that keeps it apart from the next block.
                case ParagraphBlock paragraph:
                    RenderContainerInline(paragraph.Inline, sb);
                    sb.AppendLine().AppendLine();
                    break;

                // A heading keeps its words and loses the hashes a plain channel would print.
                case HeadingBlock heading:
                    RenderContainerInline(heading.Inline, sb);
                    sb.AppendLine().AppendLine();
                    break;

                // A code block's lines verbatim, read up to their count: the line array keeps spare room that would print as empty lines.
                case CodeBlock code:
                    for (int lineIndex = 0; lineIndex < code.Lines.Count; lineIndex++)
                        sb.AppendLine(code.Lines.Lines[lineIndex].ToString());
                    sb.AppendLine();
                    break;

                // A list or a quote holds blocks of its own, whose words are kept.
                case ContainerBlock nested:
                    RenderContainerBlock(nested, sb);
                    break;
            }
        }
    }

    /// <summary>Writes the words of an inline container: literals and code as they are, line breaks as new lines, a link's text or address.</summary>
    private static void RenderContainerInline(ContainerInline? containerInline, StringBuilder sb)
    {
        // A block with no inline content contributes no words.
        if (containerInline == null)
            return;

        // An inline of any other kind, such as raw HTML, is left out.
        foreach (Inline inline in containerInline)
        {
            switch (inline)
            {
                // Plain words, which are all a plain channel can show.
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;

                // Inline code keeps its text and loses the backticks a plain channel would print.
                case CodeInline code:
                    sb.Append(code.Content);
                    break;

                // A line break of either kind, since the author meant the words on two lines.
                case LineBreakInline:
                    sb.AppendLine();
                    break;

                // A bare address has no other text, so it is kept to stay usable.
                case AutolinkInline autolink:
                    sb.Append(autolink.Url);
                    break;

                // A link keeps its visible text, its address only cluttering the line.
                case LinkInline link:
                    RenderContainerInline(link, sb);
                    break;

                // Emphasis keeps its words and loses its asterisks.
                case ContainerInline nested:
                    RenderContainerInline(nested, sb);
                    break;
            }
        }
    }
}