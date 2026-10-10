using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Services;

/// <summary>
/// IPresenterService implementation: generates presentation via LLM or falls back to config.
/// Caches per-channel (same intents + capabilities = same outcome) and never throws — LLM
/// failures route to a deterministic fallback (the Fallback message + intent-derived quick replies).
/// </summary>
public class LLMPresenterService : IPresenterService
{
    /// <summary>
    /// Stands in for a conversation id on the presenter's model calls and channel cache, since a welcome
    /// is composed before any conversation exists: no dust is charged to it and logs can tell it apart.
    /// </summary>
    private const string PresentationLabel = "presentation";

    /// <summary>
    /// LLM used to author the welcome message and its quick replies. Consumed through the
    /// stateless completion path, so it always runs on the framework tier.
    /// </summary>
    private readonly ILLMService llmService;

    /// <summary>
    /// Source of the <c>Presentation</c> prompt: the sections that ask the model for the welcome,
    /// plus the <c>Fallback</c> and <c>NoAgents</c> messages that the two non-LLM paths return verbatim.
    /// </summary>
    private readonly IPromptResolverService promptResolverService;

    /// <summary>
    /// Registry of per-conversation handshakes, queried to turn the caller's conversationId into
    /// the originating channel's name (the cache key) and capability budget.
    /// </summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>
    /// The same degradation pass every outbound message goes through, applied here before caching
    /// so the cached value is exactly what a real send would have produced for this channel.
    /// </summary>
    private readonly MorganaChannelAdapter channelAdapter;

    /// <summary>
    /// Logger for cache misses, LLM outcomes and fallback activations — the only visibility into
    /// which of the three paths produced a given presentation.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Per-channel presentation cache: Lazy&lt;Task&lt;PresentationResult&gt;&gt; per key.
    /// ConcurrentDictionary.GetOrAdd + Lazy ensures compute-once per channel under concurrent
    /// first-callers without explicit locks: GetOrAdd runs factory multiple times but persists
    /// one Lazy; Lazy runs its factory exactly once; concurrent callers await the same task.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<Records.PresentationResult>>> cache = new();

    /// <param name="llmService">LLM service used to generate the presentation; always runs on the framework tier.</param>
    /// <param name="promptResolverService">Prompt resolver used to load the <c>Presentation</c> prompt (message template, <c>Fallback</c> and <c>NoAgents</c> messages).</param>
    /// <param name="channelMetadataStore">Resolves the originating channel's name/capabilities, so callers only pass conversationId.</param>
    /// <param name="channelAdapter">Same capability-driven degradation chain any outbound message goes through.</param>
    /// <param name="logger">Logger for generation, cache-miss and fallback diagnostics.</param>
    public LLMPresenterService(
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IChannelMetadataStore channelMetadataStore,
        MorganaChannelAdapter channelAdapter,
        ILogger logger)
    {
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        this.channelMetadataStore = channelMetadataStore;
        this.channelAdapter = channelAdapter;
        this.logger = logger;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// The conversation has no handshake on record: not an LLM failure, so it is surfaced rather than
    /// routed to the fallback.
    /// </exception>
    public async Task<Records.PresentationResult> GenerateAsync(
        IReadOnlyList<Records.IntentDefinition> displayableIntents,
        string conversationId)
    {
        // The originating channel decides what the presentation may carry
        ChannelMetadata channelMetadata = await channelMetadataStore.GetChannelMetadataAsync(conversationId);

        // The capabilities are the only part of the handshake that shapes the presentation.
        ChannelCapabilities channelCapabilities = channelMetadata.Capabilities;

        // One presentation per channel: the first conversation to arrive pays for it and every later
        // one, including a concurrent one, receives the same result.
        return await cache.GetOrAdd(
            channelMetadata.Coordinates.ChannelName,
            key => new Lazy<Task<Records.PresentationResult>>(
                        () => BuildPresentationResultAsync(displayableIntents, channelCapabilities, key))
        ).Value;
    }

    /// <summary>
    /// Generates the initial presentation and runs it through the canonical adaptation chain so that
    /// the cached value is exactly what a real outbound send would produce for this channel.
    /// </summary>
    /// <param name="displayableIntents">Intents to turn into quick replies; an empty list yields the prompt's <c>NoAgents</c> message and no buttons.</param>
    /// <param name="channelCapabilities">Feature budget of the originating channel, driving the degradation pass.</param>
    /// <param name="channelName">Cache key and channel identity, also used to synthesise the adapter's placeholder conversation id.</param>
    /// <returns>The presentation already degraded to what this channel can actually render.</returns>
    private async Task<Records.PresentationResult> BuildPresentationResultAsync(
        IReadOnlyList<Records.IntentDefinition> displayableIntents,
        ChannelCapabilities channelCapabilities,
        string channelName)
    {
        // Marks the one conversation per channel that pays for the presentation.
        logger.LogInformation(
            "LLMPresenterService: building presentation for channel '{ChannelName}' (cache miss)", channelName);

        // Morgana must always present herself: with no agent the prompt's own message stands in for the model's.
        Records.Prompt presentationPrompt = await promptResolverService.ResolveAsync(Constants.Prompts.Presentation);
        Records.PresentationResult presentationResult = displayableIntents.Count == 0
            ? new Records.PresentationResult(presentationPrompt.GetMessage(Constants.Messages.NoAgents), [])
            : await GenerateMessageAsync(presentationPrompt, displayableIntents);

        // Capability-driven degradation with caching of the outcome: subsequent conversations on this channel pay zero LLM cost.
        ChannelMessage channelMessage = await channelAdapter.AdaptAsync(
            new ChannelMessage
            {
                ConversationId = $"{channelName}-{PresentationLabel}-cache",
                Text = presentationResult.Message,
                MessageType = ChannelMessageTypes.Presentation,
                QuickReplies = presentationResult.QuickReplies,
                AgentName = Constants.Morgana,
                AgentCompleted = false
            }, channelCapabilities);

        // The presentation never carries a rich card and the supervisor's send path does not read one from it.
        return new Records.PresentationResult(channelMessage.Text, channelMessage.QuickReplies ?? []);
    }

    /// <summary>
    /// Invokes the LLM to produce the initial presentation. On any failure (network error, bad JSON,
    /// null payload) it logs and returns the deterministic fallback so the caller never sees an
    /// exception — the service's reliability contract is enforced here.
    /// </summary>
    /// <param name="presentationPrompt">Resolved <c>Presentation</c> prompt; it also carries the fallback text.</param>
    /// <param name="displayableIntents">Intents handed to the model as a bullet list and reused verbatim by the fallback path.</param>
    /// <returns>The LLM-generated presentation, or the deterministic fallback if anything went wrong.</returns>
    private async Task<Records.PresentationResult> GenerateMessageAsync(
        Records.Prompt presentationPrompt,
        IReadOnlyList<Records.IntentDefinition> displayableIntents)
    {
        try
        {
            // The model learns what Morgana can do from one bullet per intent: its name and its description.
            string formattedIntents = string.Join("\n",
                displayableIntents.Select(i => $"- {i.Name}: {i.Description}"));

            // The three authored sections say how to welcome; what there is to offer arrives as the input.
            string presentationSystemPrompt =
                string.Join("\n\n",
                    Records.Prompt.Labeled(Constants.SectionLabels.Target, presentationPrompt.Target),
                    Records.Prompt.Labeled(Constants.SectionLabels.Instructions, presentationPrompt.Instructions),
                    Records.Prompt.Labeled(Constants.SectionLabels.Formatting, presentationPrompt.Formatting));

            // The intent list is the model's input, as the message to adapt is the channel adapter's.
            string llmResponse = await llmService.CompleteWithSystemPromptAsync(
                PresentationLabel, presentationSystemPrompt, formattedIntents);

            // A null payload carries no presentation: it is treated as a failure so that the fallback answers.
            Records.PresentationResponse? presentation =
                JsonSerializer.Deserialize<Records.PresentationResponse>(llmResponse, Records.DefaultJsonSerializerOptions)
                 ?? throw new InvalidOperationException("LLM returned null presentation");

            // The model's buttons become the quick replies that every channel receives.
            List<QuickReply> quickReplies = [.. presentation.QuickReplies.Select(qr => new QuickReply(qr.Id, qr.Label, qr.Value))];

            // Distinguishes a generated presentation from the fallback in the log.
            logger.LogInformation(
                "LLMPresenterService: LLM generated presentation with {Count} quick replies", quickReplies.Count);

            // The welcome the model wrote and the buttons that lead to the agents.
            return new Records.PresentationResult(presentation.Message, quickReplies);
        }
        catch (Exception ex)
        {
            // Any failure routes to the deterministic fallback: the user must always be greeted and so the error is logged and never propagated.
            logger.LogError(ex, "LLMPresenterService: LLM generation failed — using fallback");
            return BuildFallbackMessage(presentationPrompt, displayableIntents);
        }
    }

    /// <summary>
    /// Builds a presentation purely from configuration: the static <c>Fallback</c> message from
    /// the Presentation prompt, plus one quick reply per displayable intent derived from its
    /// label and default value. This path makes no LLM call and cannot fail — it's the safety net
    /// that lets the service guarantee its never-throw contract.
    /// </summary>
    /// <param name="presentationPrompt">Resolved <c>Presentation</c> prompt, read here only for its <c>Fallback</c> message.</param>
    /// <param name="displayableIntents">One quick reply is derived per intent from its <c>Label</c> and <c>DefaultValue</c>.</param>
    /// <returns>The presentation a user sees when no model wrote one.</returns>
    private Records.PresentationResult BuildFallbackMessage(
        Records.Prompt presentationPrompt,
        IReadOnlyList<Records.IntentDefinition> displayableIntents)
    {
        // The greeting authored for exactly this case: a real welcome somebody wrote in morgana.json,
        // never an error string, because the user is opening a conversation rather than meeting a fault.
        string fallbackMessage = presentationPrompt.GetMessage(Constants.Messages.Fallback);

        // One quick reply per intent, worded as the domain authored it.
        List<QuickReply> fallbackReplies =
        [
            .. displayableIntents
                .Select(intent => new QuickReply(intent.Name, intent.Label, intent.DefaultValue))
        ];

        // Distinguishes the fallback from a generated presentation in the log.
        logger.LogInformation(
            "LLMPresenterService: fallback presentation with {Count} quick replies", fallbackReplies.Count);

        // The authored greeting with one button per intent: what a user sees when no model wrote one.
        return new Records.PresentationResult(fallbackMessage, fallbackReplies);
    }
}