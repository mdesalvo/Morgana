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
                    ConversationMetadata = channelMessage.ConversationMetadata
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
        return BuildTemplateFallback(channelMessage, channelCapabilities);
    }

    // ── Short-circuit predicate ───────────────────────────────────────────────────

    private static bool FitsWithin(ChannelMessage channelMessage, ChannelCapabilities channelCapabilities)
    {
        if (channelMessage.RichCard != null && !channelCapabilities.SupportsRichCards)
            return false;

        if (channelMessage.QuickReplies is { Count: > 0 } && !channelCapabilities.SupportsQuickReplies)
            return false;

        if (!channelCapabilities.SupportsMarkdown && ContainsMarkdown(channelMessage.Text))
            return false;

        if (channelCapabilities.MaxMessageLength is { } max && EstimateVisualCost(channelMessage) > max)
            return false;

        return true;
    }

    // Sums visual cost of all message components (text, rich card, quick replies).
    private static int EstimateVisualCost(ChannelMessage channelMessage) =>
        channelMessage.Text.Length
      + (channelMessage.RichCard?.EstimateCost() ?? 0)
      + (channelMessage.QuickReplies?.Sum(quickReply => quickReply.EstimateCost()) ?? 0);

    // Detects markdown via Markdig parser: plain text = ParagraphBlock + LiteralInline only.
    private static bool ContainsMarkdown(string text)
    {
        MarkdownDocument document = Markdown.Parse(text);
        return document.Descendants()
                       .Any(node => node is not ParagraphBlock && node is not LiteralInline);
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

    private static ChannelMessage BuildTemplateFallback(
        ChannelMessage channelMessage,
        ChannelCapabilities channelCapabilities)
    {
        // When the channel cannot carry a rich card, we deliberately drop it here:
        // title + subtitle in isolation (without the component payload) would look alien
        // next to the narrative text. The happy path's LLM rewrite is the only place
        // capable of transcoding a card into prose — if we're in the template fallback,
        // the LLM call already failed, and an honestly incomplete message beats a message
        // with orphaned metadata.
        StringBuilder sb = new StringBuilder();
        sb.Append(channelMessage.Text);

        if (channelMessage.QuickReplies is { Count: > 0 } && !channelCapabilities.SupportsQuickReplies)
        {
            if (sb.Length > 0)
                sb.AppendLine().AppendLine();
            // Inline quick replies as plain prose for channels that have no button widget.
            // The "Options: A / B / C" format keeps them scannable without any markdown.
            sb.Append("Options: ");
            sb.Append(string.Join(" / ", channelMessage.QuickReplies.Select(r => r.Label)));
        }

        string text = sb.ToString();

        if (!channelCapabilities.SupportsMarkdown)
            text = StripMarkdown(text);

        text = EnforceLengthBudget(text, channelCapabilities);

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
            ConversationMetadata = channelMessage.ConversationMetadata
        };
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