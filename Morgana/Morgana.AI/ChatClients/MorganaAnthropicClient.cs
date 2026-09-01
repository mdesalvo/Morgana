using System.Diagnostics;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Morgana.AI.ChatClients;

/// <summary>
/// Defensive decorator over the AnthropicClient <see cref="IChatClient"/> that diagnoses and, when
/// strictly necessary, normalizes the message list immediately before the HTTP call.
/// </summary>
/// <remarks>
/// Claude 4.6+ enforces constraint: requests must end with user message, not assistant (no "prefill").
/// This guard normalizes message sequences: User/Tool roles→forward unchanged; System→rewrite to User
/// (preserves summarization prompts appended as trailing system messages); Assistant with TextContent→rewrite
/// to User (preserves model's own prior text); Assistant without TextContent→strip (no semantic payload).
/// Mirrors the strategy Anthropic SDK already uses for Tool messages (translates to user role with tool_result).
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
        List<ChatMessage> chatMessagesList = [.. chatMessages];

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
                List<AIContent> textContents =
                [
                    .. trailing.Contents.OfType<TextContent>()
                ];

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
    /// Applies the Anthropic ephemeral prompt cache marker (1h TTL) to the system prefix of the
    /// outbound request. Two paths, detailed inline: <c>ChatOptions.Instructions</c> (the agent
    /// path — promoted to a leading System message) and an already-leading System
    /// <see cref="ChatMessage"/> (Guard/Classifier/Presenter/ChannelAdapter). No-op on content
    /// below Anthropic's cacheable-size floor — harmless, not worth special-casing.
    /// </summary>
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

            // The per-turn held-context declaration (marked with Constants.Markers.DynamicInstructions) rides at
            // the tail of Instructions. Split it off so only the stable framework+domain prefix is
            // cached — otherwise every turn's declaration change would bust the whole prompt's cache.
            int markerIndex = chatOptions.Instructions.IndexOf(
                Constants.Markers.DynamicInstructions, StringComparison.Ordinal);

            // No marker means no held-context declaration was injected this turn (empty session) —
            // the whole string is the static prefix, same as before this split existed.
            string staticPrefix = markerIndex < 0 ? chatOptions.Instructions : chatOptions.Instructions[..markerIndex];
            TextContent staticContent = new TextContent(staticPrefix).WithCacheControl(Ttl.Ttl1h);
            List<AIContent> systemContents = [staticContent];

            if (markerIndex >= 0)
            {
                // Deliberately no cache_control here: Anthropic caches everything up to and including
                // a marked block, so a second, unmarked block just rides along uncached — exactly what
                // we want for text that changes every turn.
                string dynamicTail = chatOptions.Instructions[(markerIndex + Constants.Markers.DynamicInstructions.Length)..];
                systemContents.Add(new TextContent(dynamicTail));
            }

            chatMessages.Insert(0, new ChatMessage(ChatRole.System, systemContents));

            return (chatMessages, clonedChatOptions);
        }

        // PATH 2 — chatOptions.Instructions was empty, so this isn't an Microsoft.Agents.AI agent
        // call: the caller (Guard, Classifier, Presenter, ChannelAdapter via
        // MorganaLLM.CompleteWithSystemPromptAsync) put its system prompt directly in chatMessages
        // instead. Find the run of leading System messages — walk from the start and stop at the
        // first non-System message, since a System message appearing later (e.g. a mid-conversation
        // summarization note) is not part of the prefix and must never be marked.
        int lastSystemIdx = -1;
        for (int i = 0; i < chatMessages.Count; i++)
        {
            if (chatMessages[i].Role == ChatRole.System)
                lastSystemIdx = i;
            else
                break;
        }
        // No leading System message at all — nothing to cache, leave the request untouched.
        if (lastSystemIdx < 0)
            return (chatMessages, chatOptions);

        // Mark the LAST TextContent of the LAST leading System message: callers may append multiple
        // system messages (e.g. base prompt + a later addendum), and Anthropic's cache breakpoint
        // covers everything up to and including the marked block — placing it last covers the whole
        // leading run in one shot.
        TextContent? lastText = chatMessages[lastSystemIdx].Contents.OfType<TextContent>().LastOrDefault();
        if (lastText is null)
            return (chatMessages, chatOptions);

        lastText.WithCacheControl(Ttl.Ttl1h);

        return (chatMessages, chatOptions);
    }

    /// <summary>
    /// Reads the Anthropic-specific cache-creation token count from the response usage and emits it
    /// as a span tag. Complements MEAI's built-in cache_read tag, which has no counterpart for writes.
    /// Non-streaming path only — see inline comments for why, and for the tag-naming and lookup
    /// choices.
    /// </summary>
    private static void EmitCacheWriteTag(UsageDetails? usageDetails)
    {
        // Called only from GetResponseAsync: the streaming path forwards chunks untouched, and cache
        // observability there comes from MEAI's own OpenTelemetryChatClient aggregating cache_read
        // across the stream — there is no single final UsageDetails to read a cache-write count from.
        if (usageDetails?.AdditionalCounts is null)
            return;

        Activity? current = Activity.Current;
        if (current is null)
            return;

        // The MEAI Anthropic adapter surfaces cache creation as an entry in AdditionalCounts, but the
        // exact key can differ across SDK versions — match heuristically on any key naming both
        // "cache" and "creation" rather than hardcoding e.g. "cache_creation_input_tokens". No match
        // is a silent no-op: MEAI's own cache_read tag remains the primary signal either way.
        foreach (KeyValuePair<string, long> kv in usageDetails.AdditionalCounts)
        {
            if (kv.Key.Contains("cache", StringComparison.OrdinalIgnoreCase)
                 && kv.Key.Contains("creation", StringComparison.OrdinalIgnoreCase))
            {
                // No OTel semantic convention exists for the write side (only cache_read is
                // standardised) — "cache_write" is our own choice, picked for symmetry with MEAI's tag.
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
        new ChatMessage(ChatRole.User, [.. contents])
        {
            AuthorName = chatMessage.AuthorName,
            CreatedAt = chatMessage.CreatedAt,
            MessageId = chatMessage.MessageId,
            AdditionalProperties = chatMessage.AdditionalProperties
        };

    private static bool IsAcceptableTrailingRole(ChatRole role) =>
        role == ChatRole.User || role == ChatRole.Tool;

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}