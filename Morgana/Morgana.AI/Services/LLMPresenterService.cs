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
/// failures route to a deterministic fallback (FallbackMessage + intent-derived quick replies).
/// </summary>
public class LLMPresenterService : IPresenterService
{
    /// <summary>
    /// LLM used to author the welcome message and its reintroduction. Consumed through the
    /// stateless completion path, so it always runs on the framework tier.
    /// </summary>
    private readonly ILanguageModelService llmService;

    /// <summary>
    /// Source of the <c>Presentation</c> prompt: the message template interpolated with
    /// <c>((intents))</c>, plus the <c>FallbackMessage</c> and <c>NoAgentsMessage</c>
    /// additional properties the two non-LLM paths return verbatim. It also yields the
    /// <c>Morgana</c> prompt, whose personality gives the welcome its voice.
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
    /// <param name="promptResolverService">Prompt resolver used to load the <c>Presentation</c> prompt (message template, <c>FallbackMessage</c>, <c>NoAgentsMessage</c>).</param>
    /// <param name="channelMetadataStore">Resolves the originating channel's name/capabilities, so callers only pass conversationId.</param>
    /// <param name="channelAdapter">Same capability-driven degradation chain any outbound message goes through.</param>
    /// <param name="logger">Logger for generation, cache-miss and fallback diagnostics.</param>
    public LLMPresenterService(
        ILanguageModelService llmService,
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

        // Single-shot per channel. The first caller for `channelName` triggers BuildPresentationResultAsync
        // through the Lazy<Task<>>; every subsequent caller re-enters the same Task and observes
        // the same materialised PresentationResult.
        ChannelCapabilities channelCapabilities = channelMetadata.Capabilities;
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
    /// <param name="displayableIntents">Intents to turn into quick replies; an empty list yields the prompt's <c>NoAgentsMessage</c> and no buttons.</param>
    /// <param name="channelCapabilities">Feature budget of the originating channel, driving the degradation pass.</param>
    /// <param name="channelName">Cache key and channel identity, also used to synthesise the adapter's placeholder conversation id.</param>
    /// <returns>The presentation already degraded to what this channel can actually render.</returns>
    private async Task<Records.PresentationResult> BuildPresentationResultAsync(
        IReadOnlyList<Records.IntentDefinition> displayableIntents,
        ChannelCapabilities channelCapabilities,
        string channelName)
    {
        logger.LogInformation(
            "LLMPresenterService: building presentation for channel '{ChannelName}' (cache miss)", channelName);

        // Generate the rich presentation message, or the fallback version in case of any blocking issues.
        // Morgana must always present herself.
        Records.Prompt presentationPrompt = await promptResolverService.ResolveAsync(Constants.Prompts.Presentation);
        // With no agent to offer, the same authored message both opens and answers a request no agent claims.
        string noAgentsMessage = presentationPrompt.GetAdditionalProperty<string>(Constants.PromptProperties.NoAgentsMessage);
        Records.PresentationResult presentationResult = displayableIntents.Count == 0
            ? new Records.PresentationResult(noAgentsMessage, noAgentsMessage, [])
            : await GenerateMessageAsync(presentationPrompt, displayableIntents);

        // Capability-driven degradation with caching of the outcome: subsequent conversations on this channel pay zero LLM cost.
        ChannelMessage channelMessage = await channelAdapter.AdaptAsync(
            new ChannelMessage
            {
                ConversationId = $"{channelName}-presentation-cache",
                Text = presentationResult.Message,
                MessageType = ChannelMessageTypes.Presentation,
                QuickReplies = presentationResult.QuickReplies,
                AgentName = Constants.Morgana,
                AgentCompleted = false
            }, channelCapabilities);

        // The reintroduction reaches the same channel under the same budget, so it is degraded once
        // and cached with the welcome. Its buttons are the welcome's own and are not read back.
        ChannelMessage reintroductionMessage = await channelAdapter.AdaptAsync(
            new ChannelMessage
            {
                ConversationId = $"{channelName}-presentation-reintroduction-cache",
                Text = presentationResult.Reintroduction,
                MessageType = ChannelMessageTypes.Presentation,
                QuickReplies = presentationResult.QuickReplies,
                AgentName = Constants.Morgana,
                AgentCompleted = true
            }, channelCapabilities);

        // Unwrap back into the presenter's domain type. RichCard is intentionally ignored here —
        // the presentation never produces one and the supervisor's send path doesn't read it
        // off PresentationResult either.
        return new Records.PresentationResult(channelMessage.Text, reintroductionMessage.Text, channelMessage.QuickReplies ?? []);
    }

    /// <summary>
    /// Invokes the LLM to produce the initial presentation. On any failure (network error, bad JSON,
    /// null payload) it logs and returns the deterministic fallback so the caller never sees an
    /// exception — the service's reliability contract is enforced here.
    /// </summary>
    /// <param name="presentationPrompt">Resolved <c>Presentation</c> prompt; its <c>((intents))</c> placeholder is interpolated here and it also carries the fallback text.</param>
    /// <param name="displayableIntents">Intents rendered into the prompt as a bullet list and reused verbatim by the fallback path.</param>
    /// <returns>The LLM-generated presentation, or the deterministic fallback if anything went wrong.</returns>
    private async Task<Records.PresentationResult> GenerateMessageAsync(
        Records.Prompt presentationPrompt,
        IReadOnlyList<Records.IntentDefinition> displayableIntents)
    {
        try
        {
            // Format the intent list as the prompt expects (one bullet per intent, name + description).
            string formattedIntents = string.Join("\n",
                displayableIntents.Select(i => $"- {i.Name}: {i.Description}"));

            // The welcome is the first thing Morgana says, so it speaks in the voice her agents speak in.
            // The classifier never borrows it, since it answers the framework. Neither does the channel adapter:
            // it rewrites messages that are mostly the agents', each in its own voice.
            Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync(Constants.Morgana);

            // The authored sections say how to welcome and Morgana's personality how it sounds; the
            // formatted list is interpolated into the template as what there is to offer.
            string presentationSystemPrompt =
                string.Join("\n\n",
                    Records.Prompt.Labeled(Constants.SectionLabels.Target, presentationPrompt.Target),
                    Records.Prompt.Labeled(Constants.SectionLabels.Personality, morganaPrompt.Personality),
                    Records.Prompt.Labeled(Constants.SectionLabels.Instructions, presentationPrompt.Instructions),
                    Records.Prompt.Labeled(Constants.SectionLabels.Formatting, presentationPrompt.Formatting))
                .Replace(Constants.Placeholders.Intents, formattedIntents);

            // The fixed user message acts as a trigger; the real instructions live in the system prompt.
            string llmResponse = await llmService.CompleteWithSystemPromptAsync(
                "presentation", presentationSystemPrompt, "Generate the presentation");

            // The LLM is asked to return strict JSON; a null result indicates either an unparseable
            // payload or an empty body — both treated as failure and routed to the fallback.
            Records.PresentationResponse? presentation =
                JsonSerializer.Deserialize<Records.PresentationResponse>(llmResponse, Records.DefaultJsonSerializerOptions)
                 ?? throw new InvalidOperationException("LLM returned null presentation");

            // Either text left empty would reach the user as a turn with no words, so the fallback answers instead.
            if (string.IsNullOrWhiteSpace(presentation.Message) || string.IsNullOrWhiteSpace(presentation.Reintroduction))
                throw new InvalidOperationException("LLM returned an empty presentation text");

            // The model wrote the texts alone: the buttons are the configured intents, never the model's.
            List<QuickReply> quickReplies = BuildIntentButtons(displayableIntents);

            logger.LogInformation(
                "LLMPresenterService: LLM generated presentation with {Count} quick replies", quickReplies.Count);

            // The welcome and the reintroduction the model wrote, with the buttons that lead to the agents.
            return new Records.PresentationResult(presentation.Message, presentation.Reintroduction, quickReplies);
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
    /// <param name="presentationPrompt">Resolved <c>Presentation</c> prompt, read here only for its <c>FallbackMessage</c> additional property.</param>
    /// <param name="displayableIntents">One quick reply is derived per intent from its <c>Label</c> and <c>DefaultValue</c>.</param>
    /// <returns>The presentation a user sees when no model wrote one.</returns>
    private Records.PresentationResult BuildFallbackMessage(
        Records.Prompt presentationPrompt,
        IReadOnlyList<Records.IntentDefinition> displayableIntents)
    {
        // The greeting authored for exactly this case: a real welcome somebody wrote in morgana.json,
        // never an error string, because the user is opening a conversation rather than meeting a fault.
        string fallbackMessage = presentationPrompt.GetAdditionalProperty<string>(Constants.PromptProperties.FallbackMessage);

        // The same buttons a generated presentation carries: they never depend on the model.
        List<QuickReply> fallbackReplies = BuildIntentButtons(displayableIntents);

        logger.LogInformation(
            "LLMPresenterService: fallback presentation with {Count} quick replies", fallbackReplies.Count);

        // The authored greeting holds no welcome of its own, so it also serves as the reintroduction.
        return new Records.PresentationResult(fallbackMessage, fallbackMessage, fallbackReplies);
    }

    /// <summary>
    /// Builds the buttons of the presentation: one per displayable intent in its configured order, which the model neither chooses nor words.
    /// </summary>
    /// <param name="displayableIntents">The intents the presentation offers.</param>
    private static List<QuickReply> BuildIntentButtons(IReadOnlyList<Records.IntentDefinition> displayableIntents)
        // The id names the intent that a press chooses and the supervisor reads it back to land the turn on that agent.
        // Label and value are worded as the domain authored them: startup refuses an intent that lacks either.
        => [.. displayableIntents.Select(intent => new QuickReply(intent.Name, intent.Label, intent.DefaultValue))];
}