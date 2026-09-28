using System.Globalization;
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
/// strip, rich card flattened to text, inline quick replies) if LLM fails. Never throws; best-effort with semantic fidelity.
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
        // ── Short-circuit: nothing to degrade ─────────────────────────────────────
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

        // ── LLM-guided rewrite ────────────────────────────────────────────────────
        try
        {
            Records.Prompt adapterPrompt = await promptResolverService.ResolveAsync(Constants.Prompts.ChannelAdapter);

            string capabilitiesJson = JsonSerializer.Serialize(
                channelCapabilities, Records.DefaultJsonSerializerOptions);

            string systemPrompt = $"{adapterPrompt.Target}\n\n{adapterPrompt.Instructions}\n\n{adapterPrompt.Formatting}"
                .Replace(Constants.Placeholders.ChannelCapabilities, capabilitiesJson);

            string userPrompt = JsonSerializer.Serialize(channelMessage, Records.DefaultJsonSerializerOptions);

            string llmResponse = await llmService.CompleteWithSystemPromptAsync(
                channelMessage.ConversationId, systemPrompt, userPrompt);

            Records.ChannelAdapterResponse? channelAdapterResponse =
                JsonSerializer.Deserialize<Records.ChannelAdapterResponse>(llmResponse, Records.DefaultJsonSerializerOptions);

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

                return new ChannelMessage
                {
                    ConversationId = channelMessage.ConversationId,
                    Text = enforcedText,
                    Timestamp = channelMessage.Timestamp,
                    MessageType = channelMessage.MessageType,
                    QuickReplies = channelCapabilities.SupportsQuickReplies
                        ? (channelAdapterResponse.QuickReplies ?? channelMessage.QuickReplies)
                        : null,
                    RichCard = channelCapabilities.SupportsRichCards ? channelMessage.RichCard : null,
                    ErrorReason = channelMessage.ErrorReason,
                    AgentName = channelMessage.AgentName,
                    AgentCompleted = channelMessage.AgentCompleted,
                    FadingMessageDurationSeconds = channelMessage.FadingMessageDurationSeconds,
                    ConversationMetadata = channelMessage.ConversationMetadata,
                    Progress = channelMessage.Progress
                };
            }

            logger.LogWarning(
                "MorganaChannelAdapter: LLM returned empty or unparseable rewrite for {ConversationId} — using template fallback",
                channelMessage.ConversationId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "MorganaChannelAdapter: LLM rewrite failed for {ConversationId} — using template fallback", channelMessage.ConversationId);
        }

        // ── Template fallback ─────────────────────────────────────────────────────
        return DegradeWithoutModel(channelMessage, channelCapabilities);
    }

    // ── Short-circuit predicate ───────────────────────────────────────────────────

    /// <summary>True when the channel can show the message exactly as it is, so it needs no degrading.</summary>
    public static bool FitsWithin(ChannelMessage channelMessage, ChannelCapabilities channelCapabilities)
    {
        // A card or buttons the channel has no widget for would reach a screen that silently leaves them out
        if (channelMessage.RichCard != null && !channelCapabilities.SupportsRichCards)
            return false;

        if (channelMessage.QuickReplies is { Count: > 0 } && !channelCapabilities.SupportsQuickReplies)
            return false;

        // Markdown on a channel that prints text verbatim shows up as literal asterisks and hashes
        if (!channelCapabilities.SupportsMarkdown && ContainsMarkdown(channelMessage.Text))
            return false;

        // The budget counts everything the message would draw, its card and buttons as well as its text
        if (channelCapabilities.MaxMessageLength is { } max && EstimateVisualCost(channelMessage) > max)
            return false;

        return true;
    }

    // Sums visual cost of all message components (text, rich card, quick replies).
    private static int EstimateVisualCost(ChannelMessage channelMessage) =>
        channelMessage.Text.Length
      + (channelMessage.RichCard?.EstimateCost() ?? 0)
      + (channelMessage.QuickReplies?.Sum(quickReply => quickReply.EstimateCost()) ?? 0);

    // Detects markdown via Markdig parser: plain text is paragraphs of literals, whose lines may
    // simply follow one another. Only a hard break (trailing spaces, backslash) is markdown syntax.
    private static bool ContainsMarkdown(string text)
    {
        MarkdownDocument document = Markdown.Parse(text);
        return document.Descendants()
                       .Any(node => node is not ParagraphBlock
                                    && node is not LiteralInline
                                    && node is not LineBreakInline { IsHard: false });
    }

    // Enforces MaxMessageLength: strip markdown first (cheaper); truncate with ellipsis if still over.
    private static string EnforceLengthBudget(string text, ChannelCapabilities channelCapabilities)
    {
        if (channelCapabilities.MaxMessageLength is not { } max || max <= 0 || text.Length <= max)
            return text;

        string plain = StripMarkdown(text);
        if (plain.Length <= max)
            return plain;

        return plain[..Math.Max(0, max - 1)] + "…";
    }

    // ── Template fallback ─────────────────────────────────────────────────────────

    /// <summary>Degrades the message to the channel's capabilities by rule alone, spending no dust; one that already fits is returned as it is.</summary>
    public static ChannelMessage DegradeWithoutModel(
        ChannelMessage channelMessage,
        ChannelCapabilities channelCapabilities)
    {
        // A history replays every answer through here, most of which the channel shows as written
        if (FitsWithin(channelMessage, channelCapabilities))
            return channelMessage;

        // The answer's own words come first: whatever the channel cannot draw is read out after them
        StringBuilder degradedText = new StringBuilder();
        degradedText.Append(channelMessage.Text);

        // A card the channel cannot draw is read out under the answer, one line per piece of text: it routinely
        // carries the very figures the answer speaks of, which would otherwise vanish without a trace
        if (channelMessage.RichCard is { } richCard && !channelCapabilities.SupportsRichCards)
        {
            if (degradedText.Length > 0)
                degradedText.AppendLine().AppendLine();
            AppendRichCardLines(richCard.Title, richCard.Subtitle, richCard.Components, degradedText);
        }

        // Buttons the channel cannot draw become one closing line naming each choice, so the user still knows
        // what may be answered: "Options: A / B / C" reads as a list without any markdown
        if (channelMessage.QuickReplies is { Count: > 0 } && !channelCapabilities.SupportsQuickReplies)
        {
            if (degradedText.Length > 0)
                degradedText.AppendLine().AppendLine();
            degradedText.Append("Options: ");
            degradedText.Append(string.Join(" / ", channelMessage.QuickReplies.Select(quickReply => quickReply.Label)));
        }

        string text = degradedText.ToString();

        // A channel without markdown would print its syntax as literal asterisks and hashes
        if (!channelCapabilities.SupportsMarkdown)
            text = StripMarkdown(text);

        // Last, so the budget is measured on everything the answer became: its card and choices included
        text = EnforceLengthBudget(text, channelCapabilities);

        // Everything but what the channel cannot draw travels unchanged: who spoke, when and in which frame
        return new ChannelMessage
        {
            ConversationId = channelMessage.ConversationId,
            Text = text,
            Timestamp = channelMessage.Timestamp,
            MessageType = channelMessage.MessageType,
            QuickReplies = channelCapabilities.SupportsQuickReplies ? channelMessage.QuickReplies : null,
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
    }

    /// <summary>Appends every piece of text a card or one of its sections puts on screen: one line each, sections recursed into.</summary>
    private static void AppendRichCardLines(string title, string? subtitle, IEnumerable<CardComponent> components, StringBuilder cardText)
    {
        // The heading opens the block as it tops the card, so the lines under it read as what it announces
        cardText.AppendLine(title);
        if (!string.IsNullOrWhiteSpace(subtitle))
            cardText.AppendLine(subtitle);

        // In the order the card draws its components, which is the order its author meant them read in
        foreach (CardComponent component in components)
        {
            switch (component)
            {
                case TextBlockComponent textBlock:
                    cardText.AppendLine(textBlock.Content);
                    break;

                // A pair keeps its label beside its figure, which alone would be a number with no meaning
                case KeyValueComponent keyValue:
                    cardText.AppendLine(CultureInfo.InvariantCulture, $"{keyValue.Key}: {keyValue.Value}");
                    break;

                // A bullet no markdown parser reads as a list, so stripping the markdown later leaves the items in place
                case ListComponent list:
                    foreach (string item in list.Items)
                        cardText.AppendLine(CultureInfo.InvariantCulture, $"• {item}");
                    break;

                // A section is a card within the card: its heading then its own components
                case SectionComponent section:
                    AppendRichCardLines(section.Title, section.Subtitle, section.Components, cardText);
                    break;

                // A grid's cells are pairs laid out side by side: in text they follow one another
                case GridComponent grid:
                    foreach (GridItem item in grid.Items)
                        cardText.AppendLine(CultureInfo.InvariantCulture, $"{item.Key}: {item.Value}");
                    break;

                // A badge's colour carries no meaning its text does not already state
                case BadgeComponent badge:
                    cardText.AppendLine(badge.Text);
                    break;

                // An image says in text only what its caption or its alternative text say; a divider says nothing
                case ImageComponent image when (image.Caption ?? image.Alt) is { Length: > 0 } imageText:
                    cardText.AppendLine(imageText);
                    break;
            }
        }
    }

    // Walks Markdig parse tree, collects literal text, preserves block structure as line breaks.
    private static string StripMarkdown(string text)
    {
        StringBuilder sb = new StringBuilder();
        RenderContainerBlock(Markdown.Parse(text), sb);
        return sb.ToString().TrimEnd();
    }

    // Walks Markdig ContainerBlock (document or nested): paragraphs/headings flattened via RenderContainerInline,
    // code blocks emitted verbatim, other containers recursed. Results separated by blank lines.
    private static void RenderContainerBlock(ContainerBlock containerBlock, StringBuilder sb)
    {
        foreach (Block block in containerBlock)
        {
            switch (block)
            {
                case ParagraphBlock paragraph:
                    RenderContainerInline(paragraph.Inline, sb);
                    sb.AppendLine().AppendLine();
                    break;

                case HeadingBlock heading:
                    RenderContainerInline(heading.Inline, sb);
                    sb.AppendLine().AppendLine();
                    break;

                case CodeBlock code:
                    foreach (var line in code.Lines.Lines)
                        sb.AppendLine(line.ToString());
                    sb.AppendLine();
                    break;

                case ContainerBlock nested:
                    RenderContainerBlock(nested, sb);
                    break;
            }
        }
    }

    // Walks Markdig ContainerInline: literals/code as-is, line breaks→newlines, links/containers recursed.
    private static void RenderContainerInline(ContainerInline? containerInline, StringBuilder sb)
    {
        if (containerInline == null) return;
        foreach (Inline inline in containerInline)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content.ToString());
                    break;

                case CodeInline code:
                    sb.Append(code.Content);
                    break;

                case LineBreakInline:
                    sb.AppendLine();
                    break;

                case AutolinkInline autolink:
                    sb.Append(autolink.Url);
                    break;

                case LinkInline link:
                    RenderContainerInline(link, sb);
                    break;

                case ContainerInline nested:
                    RenderContainerInline(nested, sb);
                    break;
            }
        }
    }
}