using System.Collections.Concurrent;
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
    private readonly ILanguageModelService llmService;

    /// <summary>
    /// Source of the <c>Presentation</c> prompt: the sections that ask the model for the welcome,
    /// plus the <c>Fallback</c> and <c>NoAgents</c> messages that the two non-LLM paths return verbatim.
    /// It also yields the <c>Morgana</c> prompt, whose personality gives the welcome its voice.
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
            // The authored text reads right on arrival and when served again on a complement classification, so one text is both.
            ? new Records.PresentationResult(
                presentationPrompt.GetMessage(Constants.Messages.NoAgents),
                presentationPrompt.GetMessage(Constants.Messages.NoAgents),
                [])
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

        // The reintroduction is degraded for the same channel with the same buttons: it is served on a complement classification and must fit the channel as the welcome does.
        ChannelMessage reintroductionMessage = await channelAdapter.AdaptAsync(
            new ChannelMessage
            {
                ConversationId = $"{channelName}-{PresentationLabel}-reintroduction-cache",
                Text = presentationResult.Reintroduction,
                MessageType = ChannelMessageTypes.Presentation,
                QuickReplies = presentationResult.QuickReplies,
                AgentName = Constants.Morgana,
                AgentCompleted = false
            }, channelCapabilities);

        // The presentation never carries a rich card and the supervisor's send path does not read one from it.
        return new Records.PresentationResult(channelMessage.Text, reintroductionMessage.Text, channelMessage.QuickReplies ?? []);
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

            // The welcome is the first thing Morgana says, so it speaks in the voice her agents speak in.
            // The guard borrows it for its refusals. Classifier and channel adapter never do: they answer the framework, not the user.
            Records.Prompt morganaPrompt = await promptResolverService.ResolveAsync(Constants.Morgana);

            // The authored sections say how to welcome and Morgana's personality how it sounds; what there is to offer arrives as the input.
            string presentationSystemPrompt =
                string.Join("\n\n",
                    Records.Prompt.Labeled(Constants.SectionLabels.Target, presentationPrompt.Target),
                    Records.Prompt.Labeled(Constants.SectionLabels.Personality, morganaPrompt.Personality),
                    Records.Prompt.Labeled(Constants.SectionLabels.Instructions, presentationPrompt.Instructions),
                    Records.Prompt.Labeled(Constants.SectionLabels.Formatting, presentationPrompt.Formatting));

            // The intent list is the model's input, as the message to adapt is the channel adapter's.
            // A null payload carries no presentation: it is treated as a failure so that the fallback answers.
            Records.PresentationResponse presentation =
                await llmService.CompleteStructuredAsync<Records.PresentationResponse>(
                    PresentationLabel, presentationSystemPrompt, formattedIntents)
                 ?? throw new InvalidOperationException("LLM returned null presentation");

            // Either text left empty would reach the user as a turn with no words, so the fallback answers instead.
            if (string.IsNullOrWhiteSpace(presentation.Message) || string.IsNullOrWhiteSpace(presentation.Reintroduction))
                throw new InvalidOperationException("LLM returned an empty presentation text");

            // The model wrote the welcome alone: the buttons are the configured intents, never the model's.
            List<QuickReply> quickReplies = BuildIntentButtons(displayableIntents);

            // Distinguishes a generated presentation from the fallback in the log.
            logger.LogInformation(
                "LLMPresenterService: LLM generated presentation with {Count} quick replies", quickReplies.Count);

            // The welcome the model wrote and the buttons that lead to the agents.
            return new Records.PresentationResult(presentation.Message, presentation.Reintroduction, quickReplies);
        }
        catch (Exception ex)
        {
            // Any failure routes to the deterministic fallback: the user must always be greeted and so the error is logged and never propagated.
            logger.LogError(ex, "LLMPresenterService: LLM generation failed — using fallback");
            return BuildFallbackMessage(presentationPrompt, displayableIntents);
        }
    }

    /// <summary>
    /// Builds the buttons of the welcome: one per displayable intent in its configured order, which the model neither chooses nor words.
    /// </summary>
    /// <param name="displayableIntents">The intents the welcome offers; each button carries the intent's name as its id and its default value as the message that a press sends.</param>
    private static List<QuickReply> BuildIntentButtons(IReadOnlyList<Records.IntentDefinition> displayableIntents)
        // The id names the intent that a press chooses and the supervisor reads it back to land the turn on that agent.
        => [.. displayableIntents.Select(intent => new QuickReply(intent.Name, intent.Label, intent.DefaultValue))];

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
        List<QuickReply> fallbackReplies = BuildIntentButtons(displayableIntents);

        // Distinguishes the fallback from a generated presentation in the log.
        logger.LogInformation(
            "LLMPresenterService: fallback presentation with {Count} quick replies", fallbackReplies.Count);

        // The authored text with one button per intent: what a user sees when no model wrote one. It reads
        // right on arrival and when served again on a complement classification, so one text is both.
        return new Records.PresentationResult(fallbackMessage, fallbackMessage, fallbackReplies);
    }
}