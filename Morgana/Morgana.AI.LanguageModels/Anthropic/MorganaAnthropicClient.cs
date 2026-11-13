using System.Diagnostics;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Morgana.AI.LanguageModels.Anthropic;

/// <summary>
/// Decorator over the Anthropic <see cref="IChatClient"/> that ends every request on a role Claude accepts and marks the system prompt for caching.
/// Since Claude 4.6 a request may not end on an assistant message (no prefill): a trailing system or assistant message with text is kept as a user one,
/// the way the SDK already sends a tool result, while a trailing assistant message without text is dropped.
/// </summary>
internal sealed class MorganaAnthropicClient : DelegatingChatClient
{
    /// <summary>Writes the role trail at Debug and every structural repair at Warning, so a malformed request upstream leaves a trace.</summary>
    private readonly ILogger logger;

    /// <summary>Wraps the SDK adapter of one tier.</summary>
    /// <param name="innerClient">The SDK's chat client of the tier.</param>
    /// <param name="loggerFactory">Writes the repairs; <c>null</c> silences them while the repairs still apply.</param>
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
        // The request Claude receives ends on a role it accepts and carries the cache marker on its stable prefix.
        List<ChatMessage> normalizedChatMessages = NormalizeForAnthropic(chatMessages);
        (normalizedChatMessages, chatOptions) = MarkLeadingSystemForCache(normalizedChatMessages, chatOptions);

        // The whole usage arrives with the answer, so the cache writes it reports can tag the span of this call.
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
        // The same repairs as an answer that is not streamed, applied before the first chunk is asked for.
        List<ChatMessage> normalizedChatMessages = NormalizeForAnthropic(chatMessages);
        (normalizedChatMessages, chatOptions) = MarkLeadingSystemForCache(normalizedChatMessages, chatOptions);

        // The chunks pass untouched: a stream has no single final usage to read the cache writes from, while
        // MEAI's OpenTelemetryChatClient above already aggregates the cache reads across it.
        return base.GetStreamingResponseAsync(normalizedChatMessages, chatOptions, cancellationToken);
    }

    /// <summary>
    /// Returns a copy of the message list that ends on a role Claude accepts, rewriting or dropping the trailing messages that do not.
    /// The role trail is logged at Debug on every call and each repair at Warning.
    /// </summary>
    private List<ChatMessage> NormalizeForAnthropic(IEnumerable<ChatMessage> chatMessages)
    {
        // A copy, since the caller's list is the agent's history and must stay as it was.
        List<ChatMessage> chatMessagesList = [.. chatMessages];

        // An empty request has no trailing role to repair; the SDK reports it.
        if (chatMessagesList.Count == 0)
            return chatMessagesList;

        // The last eight roles show how the request ends, computed only when someone reads them.
        if (logger.IsEnabled(LogLevel.Debug))
        {
            string lastEightRoles = string.Join(" → ", chatMessagesList.TakeLast(8).Select(m => m.Role.Value));
            logger.LogDebug(
                "Anthropic.MorganaAnthropicClient: outbound message count={Count}, last-8 role trail: {Trail}", chatMessagesList.Count, lastEightRoles);
        }

        // A user message ends an ordinary turn and a tool message reaches Claude as a user message carrying tool_result blocks.
        if (IsAcceptableTrailingRole(chatMessagesList[^1].Role))
            return chatMessagesList;

        // Anything else at the end is refused by Claude, so the whole role trail is logged before the repair.
        string fullListOfRoles = string.Join(" → ", chatMessagesList.Select(m => m.Role.Value));
        logger.LogWarning(
            "Anthropic.MorganaAnthropicClient: trailing message has role={Role}, " +
            "which Claude 4.6+ rejects in trailing position (no-prefill constraint). " +
            "Full role trail: {FullTrail}", chatMessagesList[^1].Role.Value, fullListOfRoles);

        // The count before the repair, reported if the repair leaves nothing.
        int originalCount = chatMessagesList.Count;

        // The trailing messages are repaired one at a time until the request ends on an accepted role.
        while (chatMessagesList.Count > 0 && !IsAcceptableTrailingRole(chatMessagesList[^1].Role))
        {
            // What the warnings show of the message being repaired.
            ChatMessage trailingMessage = chatMessagesList[^1];
            string textPreview = Truncate(trailingMessage.Text, 120);
            string contentTypes = string.Join(",",
                trailingMessage.Contents.Select(c => c.GetType().Name));
            int contentCount = trailingMessage.Contents.Count;

            // No Morgana caller ends on a system message, the reducer included, but the adapter hoists only leading ones
            // and would lose a trailing one: its instruction is kept as a user message, on a clone since the history holds the original.
            if (trailingMessage.Role == ChatRole.System)
            {
                chatMessagesList[^1] = CloneAsUser(trailingMessage, trailingMessage.Contents);
                logger.LogWarning(
                    "Anthropic.MorganaAnthropicClient: rewrote trailing system message to user " +
                    "[content-types=[{ContentTypes}], text-preview=\"{TextPreview}\"] — " +
                    "MEAI's Anthropic adapter only hoists leading system messages",
                    contentTypes, textPreview);
                break;
            }

            // A trailing assistant message is the prefill Claude refuses: its text, if any, is kept as a user message.
            if (trailingMessage.Role == ChatRole.Assistant)
            {
                // Only the text carries meaning for the next answer; a tool call without its result would be refused on its own.
                List<AIContent> textContents =
                [
                    .. trailingMessage.Contents.OfType<TextContent>()
                ];

                // Nothing to keep, so the message is dropped and the next one is examined.
                if (textContents.Count == 0)
                {
                    logger.LogWarning(
                        "Anthropic.MorganaAnthropicClient: stripping trailing assistant with no TextContent " +
                        "[content-types=[{ContentTypes}]] — pure prefill artifact, nothing to preserve",
                        contentTypes);
                    chatMessagesList.RemoveAt(chatMessagesList.Count - 1);
                    continue;
                }

                // The model's own text reaches Claude as a user message, on a clone since the history holds the original.
                chatMessagesList[^1] = CloneAsUser(trailingMessage, textContents);
                logger.LogWarning(
                    "Anthropic.MorganaAnthropicClient: rewrote trailing assistant to user " +
                    "(kept {KeptCount}/{OriginalCount} content blocks: TextContent only) " +
                    "[text-preview=\"{TextPreview}\"]",
                    textContents.Count, contentCount, textPreview);
                break;
            }

            // A role with no repair of its own cannot be sent last, so it is dropped with a warning naming it.
            logger.LogWarning(
                "Anthropic.MorganaAnthropicClient: stripping trailing message with unhandled role " +
                "[role={Role}, content-types=[{ContentTypes}], text-preview=\"{TextPreview}\"]",
                trailingMessage.Role.Value, contentTypes, textPreview);
            chatMessagesList.RemoveAt(chatMessagesList.Count - 1);
        }

        // A repair that left nothing means a malformed request upstream, reported here where its cause is still visible.
        if (chatMessagesList.Count == 0)
        {
            logger.LogError(
                "Anthropic.MorganaAnthropicClient: normalization stripped every message ({Original} → 0). " +
                "The HTTP call will likely fail; this indicates an upstream malformed request.",
                originalCount);
        }

        // The request as Claude will accept it.
        return chatMessagesList;
    }

    /// <summary>
    /// Marks the stable system prefix of the request for Anthropic's 1-hour prompt cache, either from the agent's instructions or from a leading system message.
    /// A prefix below Anthropic's cacheable size is simply not cached, which costs nothing.
    /// </summary>
    private static (List<ChatMessage> ChatMessages, ChatOptions? ChatOptions) MarkLeadingSystemForCache(
        List<ChatMessage> chatMessages,
        ChatOptions? chatOptions)
    {
        // An agent's run carries its prompt in the options' instructions: it becomes a leading system message carrying the marker.
        // The instructions are cleared on a clone so that Claude does not receive the prefix twice.
        if (!string.IsNullOrEmpty(chatOptions?.Instructions))
        {
            ChatOptions clonedChatOptions = chatOptions.Clone();
            clonedChatOptions.Instructions = null;

            // The per-turn tail opens with Constants.Markers.DynamicInstructions: only what precedes it is cached,
            // otherwise every turn's held-context declaration would invalidate the whole prompt's cache.
            int markerIndex = chatOptions.Instructions.IndexOf(
                Constants.Markers.DynamicInstructions, StringComparison.Ordinal);

            // Without a marker the turn carries no per-turn tail and the whole prompt is the stable prefix.
            string staticPrefix = markerIndex < 0 ? chatOptions.Instructions : chatOptions.Instructions[..markerIndex];
            TextContent staticContent = new TextContent(staticPrefix).WithCacheControl(Ttl.Ttl1h);
            List<AIContent> systemContents = [staticContent];

            // The tail rides as a second block without a marker: Anthropic caches up to the marked block and reads the rest fresh.
            if (markerIndex >= 0)
            {
                string dynamicTail = chatOptions.Instructions[(markerIndex + Constants.Markers.DynamicInstructions.Length)..];
                systemContents.Add(new TextContent(dynamicTail));
            }

            // The system message opens the request, where the adapter hoists it to Claude's system parameter.
            chatMessages.Insert(0, new ChatMessage(ChatRole.System, systemContents));

            // The request without instructions, which now live in the leading system message.
            return (chatMessages, clonedChatOptions);
        }

        // A framework actor's call through CompleteWithSystemPromptAsync carries its prompt as leading system messages instead.
        // Only the leading run is the prefix: a system message appearing later is never marked.
        int lastSystemIndex = -1;
        for (int messageIndex = 0; messageIndex < chatMessages.Count; messageIndex++)
        {
            // The run ends at the first message that is not a system one.
            if (chatMessages[messageIndex].Role == ChatRole.System)
                lastSystemIndex = messageIndex;
            else
                break;
        }

        // A request with no leading system message has no prefix to cache.
        if (lastSystemIndex < 0)
            return (chatMessages, chatOptions);

        // The marker goes on the last text of the last leading system message, since the cache covers everything up to the marked block.
        TextContent? lastTextContent = chatMessages[lastSystemIndex].Contents.OfType<TextContent>().LastOrDefault();

        // A system message without text has nothing to mark.
        if (lastTextContent is null)
            return (chatMessages, chatOptions);

        // The marker is set on the content in place: the caller built these messages for this one call.
        lastTextContent.WithCacheControl(Ttl.Ttl1h);

        // The request with its prefix marked.
        return (chatMessages, chatOptions);
    }

    /// <summary>
    /// Tags the span of a call with the prompt tokens it wrote into the cache, which MEAI's own cache_read tag has no counterpart for.
    /// It runs for an answer that is not streamed only, since a stream has no single final usage to read.
    /// </summary>
    private static void EmitCacheWriteTag(UsageDetails? usageDetails)
    {
        // A usage without extra counts reports no cache write.
        if (usageDetails?.AdditionalCounts is null)
            return;

        // Outside a trace there is no span to tag.
        Activity? currentActivity = Activity.Current;
        if (currentActivity is null)
            return;

        // The same count that the dust ledger prices, so the tag and the charge never disagree on the cache writes.
        // A call that wrote nothing to the cache reports no count and tags nothing.
        if (!usageDetails.AdditionalCounts.TryGetValue(Constants.UsageCounts.CacheCreationInputTokens, out long cacheCreationTokens))
            return;

        // OpenTelemetry standardises only the cache read: the write's name mirrors MEAI's tag for it.
        currentActivity.SetTag("gen_ai.usage.cache_write.input_tokens", cacheCreationTokens);
    }

    /// <summary>A user message carrying <paramref name="contents"/> and the author, date, id and properties of <paramref name="chatMessage"/>, sharing no list with it.</summary>
    private static ChatMessage CloneAsUser(ChatMessage chatMessage, IEnumerable<AIContent> contents) =>
        new ChatMessage(ChatRole.User, [.. contents])
        {
            AuthorName = chatMessage.AuthorName,
            CreatedAt = chatMessage.CreatedAt,
            MessageId = chatMessage.MessageId,
            AdditionalProperties = chatMessage.AdditionalProperties
        };

    /// <summary>Whether Claude accepts a request ending on <paramref name="role"/>: a user message or a tool result, which reaches it as one.</summary>
    private static bool IsAcceptableTrailingRole(ChatRole role) =>
        role == ChatRole.User || role == ChatRole.Tool;

    /// <summary>The first <paramref name="max"/> characters of a text for a log line, with an ellipsis when cut.</summary>
    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}