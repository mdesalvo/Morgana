using System.Diagnostics;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Morgana.AI.Abstractions.LLMs;

/// <summary>
/// Defensive decorator over the AnthropicClient <see cref="IChatClient"/> that diagnoses and, when
/// strictly necessary, normalizes the message list immediately before the HTTP call.
/// </summary>
/// <remarks>
/// <para><strong>Why it exists:</strong> Claude 4.6+ (Sonnet 4.6, Opus 4.6/4.7, ...)
/// reject any request whose final message has role <c>assistant</c> with the error
/// <c>"This model does not support assistant message prefill. The conversation must end with a
/// user message."</c> Sonnet 4.5 still accepts the old prefill pattern, so the constraint is
/// strictly Anthropic-side and version-dependent.</para>
///
/// <para><strong>Behaviour:</strong></para>
/// <list type="number">
///   <item>Always emits a <c>Debug</c> log with the trailing role sequence (last 8 messages).
///     This is the diagnostic channel: enabling <c>Debug</c> on this category lets us see exactly
///     what Microsoft.Agents.AI is handing to the Anthropic SDK on the call that fails.</item>
///   <item>If the final message is <see cref="ChatRole.User"/> or <see cref="ChatRole.Tool"/>,
///     the list is forwarded untouched. Tool-role messages translate to a
///     <c>user</c>-role message containing a <c>tool_result</c> block in the Anthropic API
///     format, so they are accepted as the trailing message.</item>
///   <item>If the final message is <see cref="ChatRole.System"/>, the role is rewritten to
///     <see cref="ChatRole.User"/> in place — the content is preserved verbatim. This is the
///     pattern produced by <c>SummarizingChatReducer</c>, which appends its summarization
///     prompt as a trailing system message; the MEAI→Anthropic adapter only hoists *leading*
///     system messages to the top-level <c>system</c> parameter, so a trailing system message
///     would otherwise be lost or misinterpreted as a prefill. Demoting it to user preserves
///     the instruction (the model treats it as a user turn) and leaves the request well-formed.</item>
///   <item>If the final message is <see cref="ChatRole.Assistant"/>, the guard mirrors the
///     same role-rewrite trick on a per-content basis. <see cref="TextContent"/> blocks carry
///     semantic information the model emitted previously and must be preserved: they are kept
///     and the message role is rewritten to <see cref="ChatRole.User"/>. The model reads its
///     own prior text as a user turn — slightly unusual, but no context is lost. Non-text
///     blocks (typically <c>FunctionCallContent</c> from a tool-only turn whose results are
///     missing) are not valid under the user role and would be the prefill artifact in
///     disguise: if a trailing assistant has no <see cref="TextContent"/> at all, the whole
///     message is stripped instead. Both branches log at <c>Warning</c> level so the artifact
///     is captured for post-mortem analysis.</item>
/// </list>
///
/// <para>This mirrors the strategy the Anthropic SDK already applies to <see cref="ChatRole.Tool"/>
/// messages (translated into a <c>user</c> message containing <c>tool_result</c> blocks): the
/// role is rewritten to satisfy the API constraint while the payload is preserved verbatim.
/// Strip is reserved for messages that carry no semantic payload at all.</para>
/// </remarks>
internal sealed class MorganaAnthropicClient : DelegatingChatClient
{
    private readonly ILogger logger;

    public MorganaAnthropicClient(IChatClient innerClient, ILoggerFactory? loggerFactory)
        : base(innerClient)
    {
        logger = loggerFactory?.CreateLogger<MorganaAnthropicClient>()
                    ?? NullLogger<MorganaAnthropicClient>.Instance;
    }

    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> normalizedChatMessages = NormalizeForAnthropic(chatMessages);
        (normalizedChatMessages, chatOptions) = MarkLeadingSystemForCache(normalizedChatMessages, chatOptions);

        ChatResponse chatResponse = await base.GetResponseAsync(normalizedChatMessages, chatOptions, cancellationToken);
        EmitCacheWriteTag(chatResponse.Usage);
        return chatResponse;
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        List<ChatMessage> normalizedChatMessages = NormalizeForAnthropic(chatMessages);
        (normalizedChatMessages, chatOptions) = MarkLeadingSystemForCache(normalizedChatMessages, chatOptions);

        // Streaming forwards chunks straight through — we do NOT inspect them. Cache-related
        // observability for the streaming path is provided by MEAI's OpenTelemetryChatClient
        // upstream, which already aggregates cache_read.input_tokens across the stream.
        // Our custom cache_creation tag (see EmitCacheCreationTag) only applies to the
        // non-streaming path, where ChatResponse.Usage is delivered atomically.
        return base.GetStreamingResponseAsync(normalizedChatMessages, chatOptions, cancellationToken);
    }

    /// <summary>
    /// Inspects the outbound message list and, if it does not end with a user-acceptable role,
    /// rewrites or strips the trailing chatMessages until it does. Diagnostic logs are always
    /// emitted at <c>Debug</c>; structural fixes are logged at <c>Warning</c>.
    /// </summary>
    private List<ChatMessage> NormalizeForAnthropic(IEnumerable<ChatMessage> chatMessages)
    {
        List<ChatMessage> chatMessagesList = chatMessages.ToList();

        if (chatMessagesList.Count == 0)
            return chatMessagesList;

        if (logger.IsEnabled(LogLevel.Debug))
        {
            string lastEightRoles = string.Join(" → ", chatMessagesList.TakeLast(8).Select(m => m.Role.Value));
            logger.LogDebug(
                "Anthropic.MorganaAnthropicClient: outbound message count={Count}, last-8 role trail: {Trail}", chatMessagesList.Count, lastEightRoles);
        }

        // Trailing user / tool chatMessages are valid for Claude 4.6+:
        // - User: standard end of a turn.
        // - Tool: translated by the Anthropic SDK into a user message with tool_result content blocks.
        if (IsAcceptableTrailingRole(chatMessagesList[^1].Role))
            return chatMessagesList;

        // Trailing assistant / system chatMessages are not valid for Claude 4.6+
        //   1) System    — typically the SummarizingChatReducer's summarization prompt
        //                  appended in trailing position. The MEAI→Anthropic adapter only
        //                  hoists *leading* system chatMessages to the top-level system param,
        //                  so a trailing system would be lost; we rewrite the role to user
        //                  and preserve the instruction (semantically equivalent for an
        //                  instruction-style prompt).
        //   2) Assistant — the prefill artifact. If the message carries TextContent blocks,
        //                  rewrite the role to user keeping only the text (mirroring the
        //                  SDK's tool→user(tool_result) trick): zero context loss. If the
        //                  message has no text at all (tool-only with missing results, or
        //                  whitespace), strip — there is no semantic payload to preserve.
        string fullListOfRoles = string.Join(" → ", chatMessagesList.Select(m => m.Role.Value));
        logger.LogWarning(
            "Anthropic.MorganaAnthropicClient: trailing message has role={Role}, " +
            "which Claude 4.6+ rejects in trailing position (no-prefill constraint). " +
            "Full role trail: {FullTrail}", chatMessagesList[^1].Role.Value, fullListOfRoles);

        int original = chatMessagesList.Count;
        while (chatMessagesList.Count > 0 && !IsAcceptableTrailingRole(chatMessagesList[^1].Role))
        {
            ChatMessage trailing = chatMessagesList[^1];
            string textPreview = Truncate(trailing.Text, 120);
            string contentTypes = string.Join(",",
                trailing.Contents.Select(c => c.GetType().Name));
            int contentCount = trailing.Contents.Count;

            // Trailing "system" message is rewritten into a "user" message: same payload,
            // role swapped. We DO NOT mutate the source — the framework or callers may still
            // hold a reference to it and expect it untouched. Replacing the slot in `list`
            // with a clone keeps the contract local to this method.
            if (trailing.Role == ChatRole.System)
            {
                chatMessagesList[^1] = CloneAsUser(trailing, trailing.Contents);
                logger.LogWarning(
                    "Anthropic.MorganaAnthropicClient: rewrote trailing system message to user " +
                    "[content-types=[{ContentTypes}], text-preview=\"{TextPreview}\"] — " +
                    "trailing system is the SummarizingChatReducer pattern; " +
                    "MEAI's Anthropic adapter only hoists leading system chatMessages",
                    contentTypes, textPreview);
                break;
            }

            // Trailing "assistant" message: rewrite to "user" if there is text content to preserve,
            // otherwise strip. Same non-mutating clone strategy as the system branch.
            if (trailing.Role == ChatRole.Assistant)
            {
                List<AIContent> textContents = trailing.Contents.OfType<TextContent>()
                                                                .Cast<AIContent>()
                                                                .ToList();

                if (textContents.Count == 0)
                {
                    // No semantic payload (tool-only or whitespace) — strip.
                    logger.LogWarning(
                        "Anthropic.MorganaAnthropicClient: stripping trailing assistant with no TextContent " +
                        "[content-types=[{ContentTypes}]] — pure prefill artifact, nothing to preserve",
                        contentTypes);
                    chatMessagesList.RemoveAt(chatMessagesList.Count - 1);
                    continue;
                }

                chatMessagesList[^1] = CloneAsUser(trailing, textContents);
                logger.LogWarning(
                    "Anthropic.MorganaAnthropicClient: rewrote trailing assistant to user " +
                    "(kept {KeptCount}/{OriginalCount} content blocks: TextContent only) " +
                    "[text-preview=\"{TextPreview}\"]",
                    textContents.Count, contentCount, textPreview);
                break;
            }

            // Unknown role we don't have a strategy for — strip with explicit warning.
            logger.LogWarning(
                "Anthropic.MorganaAnthropicClient: stripping trailing message with unhandled role " +
                "[role={Role}, content-types=[{ContentTypes}], text-preview=\"{TextPreview}\"]",
                trailing.Role.Value, contentTypes, textPreview);
            chatMessagesList.RemoveAt(chatMessagesList.Count - 1);
        }

        // Defensive: if normalization stripped everything, surface the anomaly. Forwarding
        // an empty list to the SDK would fail with a less informative error downstream.
        if (chatMessagesList.Count == 0)
        {
            logger.LogError(
                "Anthropic.MorganaAnthropicClient: normalization stripped every message ({Original} → 0). " +
                "The HTTP call will likely fail; this indicates an upstream malformed request.",
                original);
        }

        return chatMessagesList;
    }

    /// <summary>
    /// Applies the Anthropic ephemeral prompt cache marker (1h TTL) to the system prefix
    /// of the outbound request, returning the (possibly mutated) message chatMessages and an
    /// (possibly cloned) <see cref="ChatOptions"/> with <see cref="ChatOptions.Instructions"/>
    /// cleared if it was promoted into a leading <see cref="ChatRole.System"/> message.
    /// </summary>
    /// <remarks>
    /// <para><strong>Two-path coverage of the system prefix:</strong></para>
    /// <chatMessages type="number">
    ///   <item><strong><c>ChatOptions.Instructions</c> path</strong> (used by
    ///     <c>Microsoft.Agents.AI</c> when an agent is invoked: the framework + domain prompt
    ///     composed by <c>MorganaAgentAdapter.ComposeAgentInstructions</c> ends up here, NOT
    ///     in the message chatMessages). When this string is non-empty, we promote it to a leading
    ///     <see cref="ChatMessage"/> with role System whose <see cref="TextContent"/> carries
    ///     the cache marker, and we clear <c>Instructions</c> on a *clone* of the chatOptions to
    ///     avoid duplicating the prefix in the API request. This is the heavy path: the
    ///     agent's system prompt is several thousand tokens.</item>
    ///   <item><strong>Leading system <see cref="ChatMessage"/> path</strong> (used by
    ///     <see cref="MorganaLLM.CompleteWithSystemPromptAsync"/> and any other caller that
    ///     puts the system prompt directly in the message chatMessages, e.g. Guard, Classifier,
    ///     Presenter, ChannelAdapter). When path 1 doesn't apply, we mark the LAST
    ///     <see cref="TextContent"/> of the LAST leading System message instead.</item>
    /// </chatMessages>
    ///
    /// <para><strong>No-op below threshold:</strong> Anthropic ignores cache breakpoints on
    /// content shorter than the model's minimum cacheable size (~1024 tokens for Sonnet,
    /// ~2048 for Haiku). Setting the marker on small system prompts (Guard, Classifier in
    /// isolation) is harmless. The big win is on agent system prompts which sit well above
    /// the threshold and are repeated turn after turn.</para>
    /// </remarks>
    private static (List<ChatMessage> ChatMessages, ChatOptions? ChatOptions) MarkLeadingSystemForCache(
        List<ChatMessage> chatMessages,
        ChatOptions? chatOptions)
    {
        // PATH 1 — Instructions on chatOptions: the Microsoft.Agents.AI agent path.
        // Promote to a leading system ChatMessage with cache marker, clear Instructions on a
        // clone to avoid the API receiving the same prefix twice.
        if (!string.IsNullOrEmpty(chatOptions?.Instructions))
        {
            ChatOptions clonedChatOptions = chatOptions.Clone();
            clonedChatOptions.Instructions = null;

            TextContent textContent = new TextContent(chatOptions.Instructions);
            textContent.WithCacheControl(Ttl.Ttl1h);
            chatMessages.Insert(0, new ChatMessage(ChatRole.System, [textContent]));

            return (chatMessages, clonedChatOptions);
        }

        // PATH 2 — leading system ChatMessage already in the chatMessages (Guard / Classifier /
        // Presenter / ChannelAdapter via MorganaLLM.CompleteWithSystemPromptAsync). Mark the
        // last TextContent of the last leading system message.
        int lastSystemIdx = -1;
        for (int i = 0; i < chatMessages.Count; i++)
        {
            if (chatMessages[i].Role == ChatRole.System)
                lastSystemIdx = i;
            else
                break;
        }
        if (lastSystemIdx < 0)
            return (chatMessages, chatOptions);

        TextContent? lastText = chatMessages[lastSystemIdx].Contents.OfType<TextContent>().LastOrDefault();
        if (lastText is null)
            return (chatMessages, chatOptions);

        lastText.WithCacheControl(Ttl.Ttl1h);

        return (chatMessages, chatOptions);
    }

    /// <summary>
    /// Reads the Anthropic-specific <c>cache_creation_input_tokens</c> counter from the
    /// response usage and emits it as a tag on <see cref="Activity.Current"/> under the key
    /// <c>gen_ai.usage.cache_write.input_tokens</c>. This complements MEAI's built-in
    /// <c>OpenTelemetryChatClient</c>, which already emits
    /// <c>gen_ai.usage.cache_read.input_tokens</c> from <see cref="UsageDetails.CachedInputTokenCount"/>
    /// but has no first-class field for cache writes.
    /// </summary>
    /// <remarks>
    /// <para>Naming choice: there is no OTel semantic convention for the cache-write side
    /// (only <c>cache_read</c> is standardised), so the tag name is up to us. We use
    /// <c>cache_write</c> for symmetry with <c>cache_read</c>; the upstream Anthropic field
    /// is <c>cache_creation_input_tokens</c>, and that name remains the source-key
    /// heuristic match below — it just isn't reflected on the public tag.</para>
    ///
    /// <para>The MEAI Anthropic adapter surfaces cache creation as an entry in
    /// <see cref="UsageDetails.AdditionalCounts"/>. The exact key may differ across SDK
    /// versions, so the lookup is heuristic: any key whose name contains both <c>"cache"</c>
    /// and <c>"creation"</c> is accepted. If no candidate is found, the method is a silent
    /// no-op — the cache_read tag from MEAI remains the primary signal.</para>
    ///
    /// <para>This method is invoked only on the non-streaming path
    /// (<see cref="GetResponseAsync"/>), where the final <see cref="ChatResponse.Usage"/>
    /// is delivered atomically. The streaming path forwards chunks untouched — for it,
    /// cache observability comes from MEAI's <c>OpenTelemetryChatClient</c> upstream,
    /// which aggregates <c>gen_ai.usage.cache_read.input_tokens</c> across the stream.</para>
    /// </remarks>
    private static void EmitCacheWriteTag(UsageDetails? usageDetails)
    {
        if (usageDetails?.AdditionalCounts is null)
            return;

        Activity? current = Activity.Current;
        if (current is null)
            return;

        foreach (KeyValuePair<string, long> kv in usageDetails.AdditionalCounts)
        {
            if (kv.Key.Contains("cache", StringComparison.OrdinalIgnoreCase)
                 && kv.Key.Contains("creation", StringComparison.OrdinalIgnoreCase))
            {
                current.SetTag("gen_ai.usage.cache_write.input_tokens", kv.Value);
                return;
            }
        }
    }

    /// <summary>
    /// Builds a new <see cref="ChatMessage"/> with role <see cref="ChatRole.User"/> from a
    /// given chat message, preserving its identifying metadata (author, timestamp, message id,
    /// additional properties). The supplied <paramref name="contents"/> are copied into a
    /// fresh list so the returned message does not share state with the chatMessage.
    /// </summary>
    private static ChatMessage CloneAsUser(ChatMessage chatMessage, IEnumerable<AIContent> contents) =>
        new ChatMessage(ChatRole.User, contents.ToList())
        {
            AuthorName = chatMessage.AuthorName,
            CreatedAt = chatMessage.CreatedAt,
            MessageId = chatMessage.MessageId,
            AdditionalProperties = chatMessage.AdditionalProperties,
        };

    private static bool IsAcceptableTrailingRole(ChatRole role) =>
        role == ChatRole.User || role == ChatRole.Tool;

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}
