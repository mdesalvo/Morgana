using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Morgana.AI.Actors;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Services;

/// <summary>
/// IPresenterService implementation: generates presentation via LLM or falls back to config.
/// Caches per-channel deterministically (same intents + capabilities = same outcome) via
/// Lazy&lt;Task&lt;&gt;&gt; and adapts via MorganaChannelAdapter. Never throws — LLM failures
/// route to deterministic fallback (FallbackMessage + intent-derived quick replies).
/// Channel resolution is internal via IChannelMetadataStore; callers pass only conversationId.
/// </summary>
public class LLMPresenterService : IPresenterService
{
    private readonly ILLMService llmService;
    private readonly IPromptResolverService promptResolverService;
    private readonly IChannelMetadataStore channelMetadataStore;
    private readonly MorganaChannelAdapter channelAdapter;
    private readonly ILogger logger;

    /// <summary>
    /// Per-channel presentation cache: Lazy&lt;Task&lt;PresentationResult&gt;&gt; per key.
    /// ConcurrentDictionary.GetOrAdd + Lazy ensures compute-once per channel under concurrent
    /// first-callers without explicit locks: GetOrAdd runs factory multiple times but persists
    /// one Lazy; Lazy runs its factory exactly once; concurrent callers await the same task.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<Records.PresentationResult>>> cache = new();

    /// <summary>
    /// Initialises a new instance of <see cref="LLMPresenterService"/>.
    /// </summary>
    /// <param name="llmService">LLM service used for presentation generation.</param>
    /// <param name="promptResolverService">Prompt resolver used to load the Presentation prompt.</param>
    /// <param name="channelMetadataStore">
    /// Registry of per-conversation channel metadata. Used to resolve the originating channel's
    /// name and capability budget so the presenter can cache (and adapt) per channel without
    /// leaking that concern to its callers.
    /// </param>
    /// <param name="channelAdapter">
    /// Canonical capability-driven adaptation chain. Invoked on the rich
    /// <see cref="ChannelMessage"/> built around the LLM-generated presentation so that
    /// resource-poor channels see the same degradation any other outbound message would receive.
    /// </param>
    /// <param name="logger">Logger for diagnostic output.</param>
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
    public Task<Records.PresentationResult> GenerateAsync(
        IReadOnlyList<Records.IntentDefinition> displayableIntents,
        string conversationId)
    {
        // Resolve the originating channel from the conversation id.
        // The store contract guarantees an entry exists once the controller gate and ConversationManagerActor have run,
        // so a miss here is an invariant violation we have to surface rather than mask.
        if (!channelMetadataStore.TryGetChannelMetadata(conversationId, out ChannelMetadata? channelMetadata))
        {
            throw new InvalidOperationException(
                $"LLMPresenterService: no channel metadata registered for conversation '{conversationId}'. " +
                "This indicates the controller gate or the ConversationManagerActor registration step was bypassed.");
        }

        // Single-shot per channel. The first caller for `channelName` triggers BuildPresentationResultAsync
        // through the Lazy<Task<>>; every subsequent caller re-enters the same Task and observes
        // the same materialised PresentationResult.
        ChannelCapabilities channelCapabilities = channelMetadata.Capabilities;
        string channelName = channelMetadata.Coordinates.ChannelName;
        return cache.GetOrAdd(
            channelName,
            key => new Lazy<Task<Records.PresentationResult>>(
                        () => BuildPresentationResultAsync(displayableIntents, channelCapabilities, key))
        ).Value;
    }

    /// <summary>
    /// Generates the initial presentation and runs it through the canonical adaptation chain so that
    /// the cached value is exactly what a real outbound send would produce for this channel.
    /// </summary>
    private async Task<Records.PresentationResult> BuildPresentationResultAsync(
        IReadOnlyList<Records.IntentDefinition> displayableIntents,
        ChannelCapabilities channelCapabilities,
        string channelName)
    {
        logger.LogInformation(
            "LLMPresenterService: building presentation for channel '{ChannelName}' (cache miss)", channelName);

        // Generate the rich presentation message, or the fallback version
        // in case of any blocking issues. Morgana must always present herself.
        Records.Prompt presentationPrompt = await promptResolverService.ResolveAsync("Presentation");
        Records.PresentationResult presentationResult = displayableIntents.Count == 0
            ? new Records.PresentationResult(presentationPrompt.GetAdditionalProperty<string>("NoAgentsMessage"), [])
            : await GenerateMessageAsync(presentationPrompt, displayableIntents);

        // Capability-driven degradation with caching of the outcome:
        // subsequent conversations on this channel pay zero LLM cost.
        ChannelMessage channelMessage = await channelAdapter.AdaptAsync(
            new ChannelMessage
            {
                ConversationId = $"{channelName}-presentation-cache",
                Text = presentationResult.Message,
                MessageType = "presentation",
                QuickReplies = presentationResult.QuickReplies,
                AgentName = "Morgana",
                AgentCompleted = false
            }, channelCapabilities);

        // Unwrap back into the presenter's domain type. RichCard is intentionally ignored here —
        // the presentation never produces one, and the supervisor's send path doesn't read it
        // off PresentationResult either.
        return new Records.PresentationResult(channelMessage.Text, channelMessage.QuickReplies ?? []);
    }

    /// <summary>
    /// Invokes the LLM to produce the initial presentation. On any failure (network error, bad JSON,
    /// null payload) it logs and returns the deterministic fallback so the caller never sees an
    /// exception — the service's reliability contract is enforced here.
    /// </summary>
    private async Task<Records.PresentationResult> GenerateMessageAsync(
        Records.Prompt presentationPrompt,
        IReadOnlyList<Records.IntentDefinition> displayableIntents)
    {
        try
        {
            // Format the intent list as the prompt expects (one bullet per intent, name + description).
            string formattedIntents = string.Join("\n",
                displayableIntents.Select(i => $"- {i.Name}: {i.Description}"));

            // Compose the system prompt by interpolating the formatted list into the template.
            string systemPrompt =
                $"{presentationPrompt.Target}\n\n{presentationPrompt.Instructions}".Replace("((intents))", formattedIntents);

            // The fixed user message acts as a trigger; the real instructions live in the system prompt.
            string llmResponse = await llmService.CompleteWithSystemPromptAsync(
                "presentation", systemPrompt, "Generate the presentation");

            // The LLM is asked to return strict JSON; a null result indicates either an unparseable
            // payload or an empty body — both treated as failure and routed to the fallback.
            Records.PresentationResponse? presentation =
                JsonSerializer.Deserialize<Records.PresentationResponse>(llmResponse, Records.DefaultJsonSerializerOptions)
                 ?? throw new InvalidOperationException("LLM returned null presentation");

            // Map the wire-format DTO into the domain QuickReply records the rest of Morgana speaks.
            List<QuickReply> quickReplies = presentation.QuickReplies
                .Select(qr => new QuickReply(qr.Id, qr.Label, qr.Value))
                .ToList();

            logger.LogInformation(
                "LLMPresenterService: LLM generated presentation with {Count} quick replies", quickReplies.Count);

            return new Records.PresentationResult(presentation.Message, quickReplies);
        }
        catch (Exception ex)
        {
            // Catch-all: any failure (LLM error, JSON parse error, null result) routes to the
            // deterministic fallback. The exception is logged but never propagated.
            logger.LogError(ex, "LLMPresenterService: LLM generation failed — using fallback");
            return BuildFallbackMessage(presentationPrompt, displayableIntents);
        }
    }

    /// <summary>
    /// Builds a presentation purely from configuration: the static <c>FallbackMessage</c> from
    /// the Presentation prompt, plus one quick reply per displayable intent derived from its
    /// label and default value. This path makes no LLM call and cannot fail — it's the safety net
    /// that lets the service guarantee its never-throw contract.
    /// </summary>
    private Records.PresentationResult BuildFallbackMessage(
        Records.Prompt presentationPrompt,
        IReadOnlyList<Records.IntentDefinition> displayableIntents)
    {
        string fallbackMessage = presentationPrompt.GetAdditionalProperty<string>("FallbackMessage");

        // One quick reply per intent. Label falls back to the intent name; value falls back to a
        // generic "Help me with X" so the button always carries a usable payload.
        List<QuickReply> fallbackReplies = displayableIntents
            .Select(intent => new QuickReply(
                intent.Name,
                intent.Label ?? intent.Name,
                intent.DefaultValue ?? $"Help me with {intent.Name}"))
            .ToList();

        logger.LogInformation(
            "LLMPresenterService: fallback presentation with {Count} quick replies", fallbackReplies.Count);

        return new Records.PresentationResult(fallbackMessage, fallbackReplies);
    }
}