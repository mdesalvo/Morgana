using System.Text;
using System.Text.Json;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Adapters;

/// <summary>
/// Transcodes a fully-featured <see cref="Records.ChannelMessage"/> into a form that
/// conforms to the expressive capabilities of the target channel. Invoked by producers
/// (presenter, supervisor, manager, controller) right before handing the message to
/// <see cref="IChannelService.SendMessageAsync"/>.
/// </summary>
/// <remarks>
/// <para><strong>Decision flow:</strong></para>
/// <list type="number">
///   <item><term>Short-circuit</term>
///     <description>If every feature in the message is supported by the channel, the input
///     is returned unchanged without touching the LLM. This is the hot path for rich
///     channels (e.g. Cauldron) and must stay free of I/O.</description></item>
///   <item><term>LLM-guided rewrite</term>
///     <description>Invokes <see cref="ILLMService"/> with the ChannelDowngrade prompt to
///     produce a semantically-equivalent plain rendering when the channel lacks rich
///     features. Morgana prompts actively push the LLM toward rich cards and quick replies,
///     so a purely syntactic strip is rarely adequate.</description></item>
///   <item><term>Template fallback</term>
///     <description>If the LLM call fails or is cancelled, falls back to a deterministic
///     in-process rewrite (strip markdown, inline quick replies, flatten rich card to
///     title + subtitle). Better ugly than silent.</description></item>
/// </list>
/// <para><strong>Scope:</strong></para>
/// <para>Whole messages only. Streaming chunks are gated upstream in <c>MorganaAgent</c>
/// (no streaming connection is ever opened toward a non-streaming channel), so this adapter
/// never sees partial chunks.</para>
/// <para><strong>Reliability contract:</strong></para>
/// <para>This service never throws. The worst case is an unstyled but semantically
/// faithful message.</para>
/// </remarks>
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
    /// Produces a <see cref="Records.ChannelMessage"/> whose features fit inside
    /// <paramref name="channelCapabilities"/>. Returns <paramref name="channelMessage"/> unchanged when no
    /// degradation is needed.
    /// </summary>
    /// <param name="channelMessage">The fully-featured outbound message as produced by the engine.</param>
    /// <param name="channelCapabilities">Expressive budget advertised by the target channel.</param>
    /// <param name="cancellationToken">Cancellation token for the async operation.</param>
    /// <returns>A channel-conformant message. Never null, never throws.</returns>
    public async Task<Records.ChannelMessage> AdaptAsync(
        Records.ChannelMessage channelMessage,
        Records.ChannelCapabilities channelCapabilities,
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
            Records.Prompt adapterPrompt = await promptResolverService.ResolveAsync("ChannelAdapter");

            string capabilitiesJson = JsonSerializer.Serialize(
                channelCapabilities, Records.DefaultJsonSerializerOptions);

            string systemPrompt = $"{adapterPrompt.Target}\n\n{adapterPrompt.Instructions}"
                .Replace("((channel_capabilities))", capabilitiesJson);

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

                return new Records.ChannelMessage
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
                    FadingMessageDurationSeconds = channelMessage.FadingMessageDurationSeconds
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

    private static bool FitsWithin(Records.ChannelMessage channelMessage, Records.ChannelCapabilities channelCapabilities)
    {
        if (channelMessage.RichCard != null && !channelCapabilities.SupportsRichCards)
            return false;

        if (channelMessage.QuickReplies is { Count: > 0 } && !channelCapabilities.SupportsQuickReplies)
            return false;

        if (!channelCapabilities.SupportsMarkdown && ContainsMarkdown(channelMessage.Text))
            return false;

        if (channelCapabilities.MaxMessageLength is { } max && channelMessage.Text.Length > max)
            return false;

        return true;
    }

    /// <summary>
    /// Detects whether <paramref name="text"/> carries any markdown structure by delegating
    /// the decision to Markdig's CommonMark parser. A plain-text string parses into a single
    /// <see cref="ParagraphBlock"/> containing only <see cref="LiteralInline"/> children;
    /// any other descendant type (heading, emphasis, code, link, list, blockquote, ...)
    /// signals the presence of real markdown syntax.
    /// </summary>
    private static bool ContainsMarkdown(string text)
    {
        MarkdownDocument document = Markdown.Parse(text);
        foreach (MarkdownObject node in document.Descendants())
        {
            if (node is not ParagraphBlock && node is not LiteralInline)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Enforces <see cref="Records.ChannelCapabilities.MaxMessageLength"/> on <paramref name="text"/>.
    /// If the text overflows, markdown is stripped first (cheaper, often enough to fit); any
    /// residual overflow is then truncated with an ellipsis, guaranteeing the cut lands on
    /// plain prose and never leaves dangling markdown syntax (<c>**text…</c>, <c>[label…</c>).
    /// Returns <paramref name="text"/> unchanged when no limit is declared or it already fits.
    /// </summary>
    private static string EnforceLengthBudget(string text, Records.ChannelCapabilities channelCapabilities)
    {
        if (channelCapabilities.MaxMessageLength is not { } max || max <= 0 || text.Length <= max)
            return text;

        string plain = StripMarkdown(text);
        if (plain.Length <= max)
            return plain;

        return plain[..Math.Max(0, max - 1)] + "…";
    }

    // ── Template fallback ─────────────────────────────────────────────────────────

    private static Records.ChannelMessage BuildTemplateFallback(
        Records.ChannelMessage channelMessage,
        Records.ChannelCapabilities channelCapabilities)
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
            sb.Append("Options: ");
            sb.Append(string.Join(" / ", channelMessage.QuickReplies.Select(r => r.Label)));
        }

        string text = sb.ToString();

        if (!channelCapabilities.SupportsMarkdown)
            text = StripMarkdown(text);

        text = EnforceLengthBudget(text, channelCapabilities);

        return new Records.ChannelMessage
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
            FadingMessageDurationSeconds = channelMessage.FadingMessageDurationSeconds
        };
    }

    /// <summary>
    /// Produces a plain-text rendering of <paramref name="text"/> by walking the Markdig
    /// parse tree and collecting only the literal content of each node. Block structure
    /// (paragraphs, headings, list items, blockquotes, code blocks) is preserved as line
    /// breaks; inline syntax (emphasis, code, links) is flattened to its visible text,
    /// dropping URLs and fence delimiters.
    /// </summary>
    private static string StripMarkdown(string text)
    {
        MarkdownDocument markdownDocument = Markdown.Parse(text);
        StringBuilder sb = new StringBuilder();
        RenderContainerBlock(markdownDocument, sb);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Walks a Markdig <see cref="ContainerBlock"/> (the document root or any nested block
    /// container) and appends its visible text to <paramref name="sb"/>. Paragraphs and
    /// headings are flattened through <see cref="RenderContainerInline"/> and separated by a
    /// blank line; code blocks are emitted verbatim line by line; any other container block
    /// (lists, blockquotes, ...) is recursed into so its leaf content still surfaces.
    /// </summary>
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

    /// <summary>
    /// Walks a Markdig <see cref="ContainerInline"/> and appends the visible text of its
    /// children to <paramref name="sb"/>. Literals and inline code are emitted as-is, line
    /// breaks become newlines, autolinks collapse to their URL, and links/other container
    /// inlines are recursed into — dropping the surrounding markdown syntax (emphasis
    /// markers, fence delimiters, link targets) while preserving the reader-visible content.
    /// Returns immediately if <paramref name="containerInline"/> is null, so callers can pass
    /// <c>ParagraphBlock.Inline</c> without a prior null check.
    /// </summary>
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