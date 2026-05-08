using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions.LLMs;

/// <summary>
/// Anthropic implementation of ILLMService.<br/>
/// Supports Claude models (claude-opus-4-5, claude-sonnet-4-5, ...)
/// </summary>
/// <remarks>
/// <para><strong>Configuration (appsettings.json):</strong></para>
/// <code>
/// {
///   "Morgana": {
///     "LLM": {
///       "Provider": "anthropic",
///       "Anthropic": {
///         "ApiKey": "sk-ant-...",
///         "Model": "claude-sonnet-4-5"
///       }
///     }
///   }
/// }
/// </code>
/// </remarks>
public class Anthropic : MorganaLLM
{
    /// <summary>
    /// Initializes a new instance of Anthropic.
    /// Creates Anthropic client and wraps it with Microsoft.Extensions.AI IChatClient,
    /// then with the in-process <see cref="GuardChatClient"/> that enforces the Claude 4.6+
    /// no-prefill constraint at the API boundary.
    /// </summary>
    /// <param name="configuration">Application configuration containing Anthropic API key and model.</param>
    /// <param name="promptResolverService">Service for resolving prompt templates.</param>
    /// <param name="loggerFactory">
    /// Optional logger factory used by <see cref="GuardChatClient"/>. When <c>null</c>, the guard's
    /// diagnostic channel is silent but the message-list normalization still applies.
    /// </param>
    public Anthropic(
        IConfiguration configuration,
        IPromptResolverService promptResolverService,
        ILoggerFactory? loggerFactory = null) : base(configuration, promptResolverService)
    {
        AnthropicClient anthropicClient = new AnthropicClient(
            new ClientOptions
            {
                ApiKey = this.configuration["Morgana:LLM:Anthropic:ApiKey"]!
            });
        string anthropicModel = this.configuration["Morgana:LLM:Anthropic:Model"]!;

        // Wrap the Anthropic client with the GuardChatClient.
        // This wrapper sits between Microsoft.Agents.AI and the SDK and enforces
        // the Claude 4.6+ no-prefill constraint by normalizing any trailing non-user message
        // right before the HTTP call.
        chatClient = new GuardChatClient(
            anthropicClient.AsIChatClient(anthropicModel), loggerFactory);
    }

    /// <summary>
    /// Defensive decorator over the Anthropic <see cref="IChatClient"/> that diagnoses and, when
    /// strictly necessary, normalizes the message list immediately before the HTTP call.
    /// </summary>
    /// <remarks>
    /// <para><strong>Why it exists:</strong> Claude 4.6 and later (Sonnet 4.6, Opus 4.6/4.7, ...)
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
    private sealed class GuardChatClient : DelegatingChatClient
    {
        private readonly ILogger logger;

        public GuardChatClient(IChatClient innerClient, ILoggerFactory? loggerFactory)
            : base(innerClient)
        {
            logger = loggerFactory?.CreateLogger<GuardChatClient>()
                        ?? NullLogger<GuardChatClient>.Instance;
        }

        /// <inheritdoc />
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return base.GetResponseAsync(NormalizeForAnthropic(messages), options, cancellationToken);
        }

        /// <inheritdoc />
        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return base.GetStreamingResponseAsync(NormalizeForAnthropic(messages), options, cancellationToken);
        }

        /// <summary>
        /// Inspects the outbound message list and, if it does not end with a user-acceptable role,
        /// rewrites or strips the trailing messages until it does. Diagnostic logs are always
        /// emitted at <c>Debug</c>; structural fixes are logged at <c>Warning</c>.
        /// </summary>
        private List<ChatMessage> NormalizeForAnthropic(IEnumerable<ChatMessage> messages)
        {
            List<ChatMessage> list = messages.ToList();

            if (list.Count == 0)
                return list;

            if (logger.IsEnabled(LogLevel.Debug))
            {
                string lastEightRoles = string.Join(" → ", list.TakeLast(8).Select(m => m.Role.Value));
                logger.LogDebug(
                    "Anthropic.GuardChatClient: outbound message count={Count}, last-8 role trail: {Trail}", list.Count, lastEightRoles);
            }

            // Trailing user / tool messages are valid for Claude 4.6+:
            // - User: standard end of a turn.
            // - Tool: translated by the Anthropic SDK into a user message with tool_result content blocks.
            if (IsAcceptableTrailingRole(list[^1].Role))
                return list;

            // Trailing assistant / system messages are not valid for Claude 4.6+
            //   1) System    — typically the SummarizingChatReducer's summarization prompt
            //                  appended in trailing position. The MEAI→Anthropic adapter only
            //                  hoists *leading* system messages to the top-level system param,
            //                  so a trailing system would be lost; we rewrite the role to user
            //                  and preserve the instruction (semantically equivalent for an
            //                  instruction-style prompt).
            //   2) Assistant — the prefill artifact. If the message carries TextContent blocks,
            //                  rewrite the role to user keeping only the text (mirroring the
            //                  SDK's tool→user(tool_result) trick): zero context loss. If the
            //                  message has no text at all (tool-only with missing results, or
            //                  whitespace), strip — there is no semantic payload to preserve.
            string fullListOfRoles = string.Join(" → ", list.Select(m => m.Role.Value));
            logger.LogWarning(
                "Anthropic.GuardChatClient: trailing message has role={Role}, " +
                "which Claude 4.6+ rejects in trailing position (no-prefill constraint). " +
                "Full role trail: {FullTrail}", list[^1].Role.Value, fullListOfRoles);

            int original = list.Count;
            while (list.Count > 0 && !IsAcceptableTrailingRole(list[^1].Role))
            {
                ChatMessage trailing = list[^1];
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
                    list[^1] = CloneAsUser(trailing, trailing.Contents);
                    logger.LogWarning(
                        "Anthropic.GuardChatClient: rewrote trailing system message to user " +
                        "[content-types=[{ContentTypes}], text-preview=\"{TextPreview}\"] — " +
                        "trailing system is the SummarizingChatReducer pattern; " +
                        "MEAI's Anthropic adapter only hoists leading system messages",
                        contentTypes, textPreview);
                    break;
                }

                // Trailing "assistant" message: rewrite to "user" if there is text to preserve,
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
                            "Anthropic.GuardChatClient: stripping trailing assistant with no TextContent " +
                            "[content-types=[{ContentTypes}]] — pure prefill artifact, nothing to preserve",
                            contentTypes);
                        list.RemoveAt(list.Count - 1);
                        continue;
                    }

                    list[^1] = CloneAsUser(trailing, textContents);
                    logger.LogWarning(
                        "Anthropic.GuardChatClient: rewrote trailing assistant to user " +
                        "(kept {KeptCount}/{OriginalCount} content blocks: TextContent only) " +
                        "[text-preview=\"{TextPreview}\"]",
                        textContents.Count, contentCount, textPreview);
                    break;
                }

                // Unknown role we don't have a strategy for — strip with explicit warning.
                logger.LogWarning(
                    "Anthropic.GuardChatClient: stripping trailing message with unhandled role " +
                    "[role={Role}, content-types=[{ContentTypes}], text-preview=\"{TextPreview}\"]",
                    trailing.Role.Value, contentTypes, textPreview);
                list.RemoveAt(list.Count - 1);
            }

            // Defensive: if normalization stripped everything, surface the anomaly. Forwarding
            // an empty list to the SDK would fail with a less informative error downstream.
            if (list.Count == 0)
            {
                logger.LogError(
                    "Anthropic.GuardChatClient: normalization stripped every message ({Original} → 0). " +
                    "The HTTP call will likely fail; this indicates an upstream malformed request.",
                    original);
            }

            return list;
        }

        /// <summary>
        /// Builds a new <see cref="ChatMessage"/> with role <see cref="ChatRole.User"/> from a
        /// source message, preserving its identifying metadata (author, timestamp, message id,
        /// additional properties). The supplied <paramref name="contents"/> are copied into a
        /// fresh list so the returned message does not share state with the source.
        /// </summary>
        private static ChatMessage CloneAsUser(ChatMessage source, IEnumerable<AIContent> contents) =>
            new ChatMessage(ChatRole.User, contents.ToList())
            {
                AuthorName = source.AuthorName,
                CreatedAt = source.CreatedAt,
                MessageId = source.MessageId,
                AdditionalProperties = source.AdditionalProperties,
            };

        private static bool IsAcceptableTrailingRole(ChatRole role) =>
            role == ChatRole.User || role == ChatRole.Tool;

        private static string Truncate(string? s, int max) =>
            string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";
    }
}