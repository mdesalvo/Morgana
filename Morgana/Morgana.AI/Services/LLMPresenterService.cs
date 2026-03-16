using System.Text.Json;
using Microsoft.Extensions.Logging;
using Morgana.AI.Actors;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IPresenterService"/> implementation providing presentation generation strategy:
/// <list type="number">
///   <item><term>LLM generation</term>
///     <description>Invokes the LLM with the Presentation system prompt and the formatted
///     intent list to produce a personalised welcome message and quick reply buttons.</description></item>
///   <item><term>Internal fallback</term>
///     <description>If the LLM call fails or returns an unparseable response, falls back to the
///     <c>FallbackMessage</c> from the Presentation prompt configuration and derives quick reply
///     buttons directly from the provided intent definitions — no further LLM call is made.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>Reliability Contract:</strong></para>
/// <para>This service never throws. All errors are caught internally, logged, and resolved
/// through the fallback path, guaranteeing that <see cref="ConversationSupervisorActor"/>
/// always receives a valid <see cref="Records.PresentationResult"/>.</para>
/// </remarks>
public class LLMPresenterService : IPresenterService
{
    private readonly ILLMService llmService;
    private readonly IPromptResolverService promptResolverService;
    private readonly ILogger logger;

    /// <summary>
    /// Initialises a new instance of <see cref="LLMPresenterService"/>.
    /// </summary>
    /// <param name="llmService">LLM service used for presentation generation.</param>
    /// <param name="promptResolverService">Prompt resolver used to load the Presentation prompt.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public LLMPresenterService(
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        ILogger logger)
    {
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task<Records.PresentationResult> GenerateAsync(
        IReadOnlyList<Records.IntentDefinition> displayableIntents)
    {
        Records.Prompt presentationPrompt = await promptResolverService.ResolveAsync("Presentation");

        // ── No agents configured ───────────────────────────────────────────────
        if (displayableIntents.Count == 0)
        {
            logger.LogWarning(
                "LLMPresenterService: no displayable intents available — returning no-agents message");

            return new Records.PresentationResult(
                presentationPrompt.GetAdditionalProperty<string>("NoAgentsMessage"), []);
        }

        // ── LLM generation ─────────────────────────────────────────────────────
        try
        {
            string formattedIntents = string.Join("\n",
                displayableIntents.Select(i => $"- {i.Name}: {i.Description}"));

            string systemPrompt =
                $"{presentationPrompt.Target}\n\n{presentationPrompt.Instructions}"
                    .Replace("((intents))", formattedIntents);

            logger.LogInformation("LLMPresenterService: invoking LLM for presentation generation");

            string llmResponse = await llmService.CompleteWithSystemPromptAsync(
                "presentation",
                systemPrompt,
                "Generate the presentation");

            Records.PresentationResponse? presentation =
                JsonSerializer.Deserialize<Records.PresentationResponse>(llmResponse);

            if (presentation != null)
            {
                logger.LogInformation(
                    "LLMPresenterService: LLM generated presentation with {Count} quick replies",
                    presentation.QuickReplies.Count);

                List<Records.QuickReply> quickReplies = presentation.QuickReplies
                    .Select(qr => new Records.QuickReply(qr.Id, qr.Label, qr.Value))
                    .ToList();

                return new Records.PresentationResult(presentation.Message, quickReplies);
            }

            throw new InvalidOperationException("LLM returned null presentation");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "LLMPresenterService: LLM generation failed — using fallback");

            return BuildFallback(presentationPrompt, displayableIntents);
        }
    }

    // ── Fallback ───────────────────────────────────────────────────────────────

    private Records.PresentationResult BuildFallback(
        Records.Prompt presentationPrompt,
        IReadOnlyList<Records.IntentDefinition> displayableIntents)
    {
        string fallbackMessage = presentationPrompt.GetAdditionalProperty<string>("FallbackMessage");

        List<Records.QuickReply> fallbackReplies = displayableIntents
            .Select(intent => new Records.QuickReply(
                intent.Name,
                intent.Label ?? intent.Name,
                intent.DefaultValue ?? $"Help me with {intent.Name}"))
            .ToList();

        logger.LogInformation(
            "LLMPresenterService: fallback presentation with {Count} quick replies",
            fallbackReplies.Count);

        return new Records.PresentationResult(fallbackMessage, fallbackReplies);
    }
}