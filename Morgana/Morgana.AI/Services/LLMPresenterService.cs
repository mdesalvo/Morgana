using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.AI.Services;

/// <summary>
/// Writes Morgana's welcome and its reintroduction by model, falling back to the authored greeting without ever throwing.
/// The result is cached per channel already adapted to it, so later conversations on that channel cost no model call.
/// </summary>
public class LLMPresenterService : IPresenterService
{
    /// <summary>The model that writes the welcome and its reintroduction, through the stateless completion on the framework tier.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>
    /// Resolves the <c>Presentation</c> prompt, with the <c>FallbackMessage</c> and <c>NoAgentsMessage</c> that the paths without a model return verbatim.
    /// It also resolves the <c>Morgana</c> prompt, whose personality gives the welcome its voice.
    /// </summary>
    private readonly IPromptResolverService promptResolverService;

    /// <summary>Turns the caller's conversation into its channel's capabilities and its name, which keys the cache.</summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>
    /// The adaptation every outbound message goes through, applied before caching.
    /// The cached presentation is then exactly what a real send would have produced for the channel.
    /// </summary>
    private readonly MorganaChannelAdapter channelAdapter;

    /// <summary>Logs the cache misses and which of the three paths wrote each presentation, its only trace.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// The presentation of each channel, written once however many conversations ask for it at the same time.
    /// Concurrent first callers may build several wrappers but only one is kept and its work runs once, awaited by all of them.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<Records.PresentationResult>>> cache = new();

    /// <summary>Binds the presenter to the model, the prompts, the handshakes and the adapter.</summary>
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
    /// <exception cref="InvalidOperationException">The conversation has no handshake on record, which is no model failure and so is not routed to the fallback.</exception>
    public async Task<Records.PresentationResult> GenerateAsync(
        IReadOnlyList<Records.IntentDefinition> displayableIntents,
        string conversationId)
    {
        // The originating channel decides what the presentation may carry.
        ChannelMetadata channelMetadata = await channelMetadataStore.GetChannelMetadataAsync(conversationId);
        ChannelCapabilities channelCapabilities = channelMetadata.Capabilities;

        // The first caller of a channel writes its presentation; every later caller receives the same one.
        return await cache.GetOrAdd(
            channelMetadata.Coordinates.ChannelName,
            key => new Lazy<Task<Records.PresentationResult>>(
                        () => BuildPresentationResultAsync(displayableIntents, channelCapabilities, key))
        ).Value;
    }

    /// <summary>Writes the presentation and adapts it to the channel, as a real send would.</summary>
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

        // The Presentation prompt carries the template and the two authored texts: Morgana always presents herself.
        Records.Prompt presentationPrompt = await promptResolverService.ResolveAsync(Constants.Prompts.Presentation);

        // With no agent to offer, the same authored message both opens and answers a request no agent claims.
        string noAgentsMessage = presentationPrompt.GetAdditionalProperty<string>(Constants.PromptProperties.NoAgentsMessage);
        Records.PresentationResult presentationResult = displayableIntents.Count == 0
            ? new Records.PresentationResult(noAgentsMessage, noAgentsMessage, [])
            : await GenerateMessageAsync(presentationPrompt, displayableIntents);

        // The welcome adapted to the channel, so later conversations on it pay for no adaptation either.
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

        // The two adapted texts with the adapted buttons: a presentation never carries a rich card.
        return new Records.PresentationResult(channelMessage.Text, reintroductionMessage.Text, channelMessage.QuickReplies ?? []);
    }

    /// <summary>
    /// Has the model write the welcome and its reintroduction, returning the authored fallback on any failure.
    /// This is where the presenter's promise never to throw is kept.
    /// </summary>
    /// <param name="presentationPrompt">Resolved <c>Presentation</c> prompt; its <c>((intents))</c> placeholder is interpolated here and it also carries the fallback text.</param>
    /// <param name="displayableIntents">Intents rendered into the prompt as a bullet list and reused verbatim by the fallback path.</param>
    /// <returns>The presentation the model wrote; the authored fallback when anything went wrong.</returns>
    private async Task<Records.PresentationResult> GenerateMessageAsync(
        Records.Prompt presentationPrompt,
        IReadOnlyList<Records.IntentDefinition> displayableIntents)
    {
        try
        {
            // The intents as the prompt lists them, one bullet each with its description.
            string formattedIntents = string.Join("\n",
                displayableIntents.Select(intent => $"- {intent.Name}: {intent.Description}"));

            // The welcome is the first thing Morgana says, so it speaks in the voice her agents speak in.
            // The UserGuard and the channel adapter borrow it too, since what they write reaches the user. The classifier,
            // the ToolGuard and the PeerGuard never do: they answer the framework.
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

            // The model answers in JSON; an empty body is a failure and goes to the fallback.
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
            // A failed call, an unreadable answer and an empty text alike end on the authored greeting.
            logger.LogError(ex, "LLMPresenterService: LLM generation failed — using fallback");
            return BuildFallbackMessage(presentationPrompt, displayableIntents);
        }
    }

    /// <summary>
    /// Builds the presentation from configuration alone: the authored <c>FallbackMessage</c> with the intents' buttons.
    /// It makes no model call and cannot fail, which is what lets the service never throw.
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

    /// <summary>Builds the presentation's buttons, one per displayable intent in its configured order, which the model neither chooses nor words.</summary>
    /// <param name="displayableIntents">The intents the presentation offers.</param>
    private static List<QuickReply> BuildIntentButtons(IReadOnlyList<Records.IntentDefinition> displayableIntents)
        // The id names the intent that a press chooses and the supervisor reads it back to land the turn on that agent.
        // Label and value are worded as the domain authored them: startup refuses an intent that lacks either.
        => [.. displayableIntents.Select(intent => new QuickReply(intent.Name, intent.Label, intent.DefaultValue))];
}