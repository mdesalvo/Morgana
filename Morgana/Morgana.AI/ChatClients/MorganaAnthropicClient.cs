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
    /// <summary>Receives the diagnostics of the outbound message list and every structural fix applied to it.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Wraps the Anthropic client of one tier.
    /// </summary>
    /// <param name="innerClient">The Anthropic client below.</param>
    /// <param name="loggerFactory">Source of the logger; null silences the diagnostics.</param>
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
        // The request must end on a role that Claude accepts, or the provider refuses the whole call.
        List<ChatMessage> normalizedChatMessages = NormalizeForAnthropic(chatMessages);

        // The system prefix is stable across calls, so it is marked for the provider's prompt cache.
        (normalizedChatMessages, chatOptions) = MarkLeadingSystemForCache(normalizedChatMessages, chatOptions);

        ChatResponse chatResponse = await base.GetResponseAsync(normalizedChatMessages, chatOptions, cancellationToken);

        // The usage of a whole response is known here: the cache writes it paid are put on the turn's span.
        EmitCacheWriteTag(chatResponse.Usage);
        return chatResponse;
    }

    /// <inheritdoc />
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        // The request must end on a role that Claude accepts, or the provider refuses the whole call.
        List<ChatMessage> normalizedChatMessages = NormalizeForAnthropic(chatMessages);

        // The system prefix is stable across calls, so it is marked for the provider's prompt cache.
        (normalizedChatMessages, chatOptions) = MarkLeadingSystemForCache(normalizedChatMessages, chatOptions);

        // The chunks reach the user untouched: a stream has no single usage report to tag and the cache
        // reads of the stream are already aggregated by the telemetry client above.
        return base.GetStreamingResponseAsync(normalizedChatMessages, chatOptions, cancellationToken);
    }

    /// <summary>
    /// Inspects the outbound message list and, if it does not end with a user-acceptable role,
    /// rewrites or strips the trailing chatMessages until it does. Diagnostic logs are always
    /// emitted at <c>Debug</c>; structural fixes are logged at <c>Warning</c>.
    /// </summary>
    private List<ChatMessage> NormalizeForAnthropic(IEnumerable<ChatMessage> chatMessages)
    {
        // The list is copied so the rewrites below never touch the caller's own.
        List<ChatMessage> chatMessagesList = [.. chatMessages];

        // An empty request has no trailing role to judge.
        if (chatMessagesList.Count == 0)
            return chatMessagesList;

        // The tail of the roles is what an operator needs to see why a request was rewritten.
        if (logger.IsEnabled(LogLevel.Debug))
        {
            string lastEightRoles = string.Join(" → ", chatMessagesList.TakeLast(8).Select(m => m.Role.Value));
            logger.LogDebug(
                "Anthropic.MorganaAnthropicClient: outbound message count={Count}, last-8 role trail: {Trail}", chatMessagesList.Count, lastEightRoles);
        }

        // A request ending on a user or a tool message is valid for Claude 4.6+ and goes out unchanged.
        if (IsAcceptableTrailingRole(chatMessagesList[^1].Role))
            return chatMessagesList;

        // A trailing system or assistant message is rejected by Claude 4.6+. The summarization prompt
        // is a trailing system message that would be lost and a trailing assistant is a prefill:
        // the loop below turns each into a user message when it holds text and drops it otherwise.
        string fullListOfRoles = string.Join(" → ", chatMessagesList.Select(m => m.Role.Value));
        logger.LogWarning(
            "Anthropic.MorganaAnthropicClient: trailing message has role={Role}, " +
            "which Claude 4.6+ rejects in trailing position (no-prefill constraint). " +
            "Full role trail: {FullTrail}", chatMessagesList[^1].Role.Value, fullListOfRoles);

        // The original size lets the log say how much of the request the normalization removed.
        int original = chatMessagesList.Count;
        while (chatMessagesList.Count > 0 && !IsAcceptableTrailingRole(chatMessagesList[^1].Role))
        {
            // Each pass judges the message that currently ends the request.
            ChatMessage trailing = chatMessagesList[^1];
            string textPreview = Truncate(trailing.Text, 120);
            string contentTypes = string.Join(",",
                trailing.Contents.Select(c => c.GetType().Name));
            int contentCount = trailing.Contents.Count;

            // A trailing system message carries an instruction that must still reach the model, so it is
            // sent as a user message. The original is left as it is: the framework still holds it.
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

            // A trailing assistant message keeps its text as a user message and loses everything else.
            if (trailing.Role == ChatRole.Assistant)
            {
                List<AIContent> textContents =
                [
                    .. trailing.Contents.OfType<TextContent>()
                ];

                if (textContents.Count == 0)
                {
                    // A tool-only or blank message carries nothing to preserve, so it is dropped and the next one is judged.
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

            // A role with no rewrite strategy is dropped, loudly, so that the request still ends acceptably.
            logger.LogWarning(
                "Anthropic.MorganaAnthropicClient: stripping trailing message with unhandled role " +
                "[role={Role}, content-types=[{ContentTypes}], text-preview=\"{TextPreview}\"]",
                trailing.Role.Value, contentTypes, textPreview);
            chatMessagesList.RemoveAt(chatMessagesList.Count - 1);
        }

        // A request emptied by the normalization is reported here, where the cause is known, rather than
        // as a less informative provider error.
        if (chatMessagesList.Count == 0)
        {
            logger.LogError(
                "Anthropic.MorganaAnthropicClient: normalization stripped every message ({Original} → 0). " +
                "The HTTP call will likely fail; this indicates an upstream malformed request.",
                original);
        }

        // The request that goes to the provider.
        return chatMessagesList;
    }

    /// <summary>
    /// Marks the system prefix of the outbound request for the provider's one-hour prompt cache, whether it
    /// arrives as the agent's instructions or as leading system messages of the framework actors.
    /// </summary>
    private static (List<ChatMessage> ChatMessages, ChatOptions? ChatOptions) MarkLeadingSystemForCache(
        List<ChatMessage> chatMessages,
        ChatOptions? chatOptions)
    {
        // An agent carries its prompt as instructions. It becomes a leading system message that can be marked
        // and the instructions are cleared on a clone so the provider never receives the prefix twice.
        if (!string.IsNullOrEmpty(chatOptions?.Instructions))
        {
            ChatOptions clonedChatOptions = chatOptions.Clone();
            clonedChatOptions.Instructions = null;

            // The agent's whole system prompt is stable for its life, so all of it is cached.
            List<AIContent> systemContents = [new TextContent(chatOptions.Instructions).WithCacheControl(Ttl.Ttl1h)];

            // The system prompt leads the request, where the provider's cache looks for a prefix.
            chatMessages.Insert(0, new ChatMessage(ChatRole.System, systemContents));

            // The marked options replace the caller's, which stay reusable across turns.
            return (chatMessages, clonedChatOptions);
        }

        // A framework actor puts its system prompt straight into the messages. Only the run of system
        // messages at the very start is the prefix: a later system note, such as a summary, must never be marked.
        int leadingSystemCount = chatMessages.TakeWhile(message => message.Role == ChatRole.System).Count();

        // A request with no leading system message has no prefix to cache.
        if (leadingSystemCount == 0)
            return (chatMessages, chatOptions);

        // The cache breakpoint covers everything up to the marked block: marking the last text of the
        // last leading system message caches the whole prefix at once.
        TextContent? lastText = chatMessages[leadingSystemCount - 1].Contents.OfType<TextContent>().LastOrDefault();

        // A system message with no text has nothing to mark.
        if (lastText is null)
            return (chatMessages, chatOptions);

        lastText.WithCacheControl(Ttl.Ttl1h);

        // The messages themselves carry the marker, so the options go on as the caller wrote them.
        return (chatMessages, chatOptions);
    }

    /// <summary>
    /// Puts the cache-creation token count of a response on the current span, beside the cache reads
    /// that the telemetry client already tags.
    /// </summary>
    private static void EmitCacheWriteTag(UsageDetails? usageDetails)
    {
        // A response whose usage reports no extra counts has no cache write to tag.
        if (usageDetails?.AdditionalCounts is null)
            return;

        // A call outside any traced turn has no span to carry the tag.
        Activity? current = Activity.Current;
        if (current is null)
            return;

        // The key that names cache creation differs across SDK versions, so any key naming both words
        // matches. No match tags nothing: the cache reads remain the primary signal.
        KeyValuePair<string, long> cacheCreation = usageDetails.AdditionalCounts.FirstOrDefault(count =>
            count.Key.Contains("cache", StringComparison.OrdinalIgnoreCase)
            && count.Key.Contains("creation", StringComparison.OrdinalIgnoreCase));
        if (cacheCreation.Key is null)
            return;

        // OpenTelemetry standardises only the read side, so the write tag is named after the read one.
        current.SetTag("gen_ai.usage.cache_write.input_tokens", cacheCreation.Value);
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

    /// <summary>True for the roles that Claude accepts at the end of a request.</summary>
    private static bool IsAcceptableTrailingRole(ChatRole role) =>
        role == ChatRole.User || role == ChatRole.Tool;

    /// <summary>Shortens a text to the length that a log line can carry.</summary>
    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
}