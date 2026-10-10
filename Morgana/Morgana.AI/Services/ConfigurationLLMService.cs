using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.ChatClients;
using Morgana.AI.Interfaces;
using Morgana.AI;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="ILLMService"/>: builds the three tiers of <c>Morgana:LLM:Tiers</c>, each on the provider that it declares.
/// </summary>
public class ConfigurationLLMService : ILLMService
{
    /// <summary>
    /// Application configuration, read for the tiers under <c>Morgana:LLM</c>, the framework tier
    /// and the telemetry switches.
    /// </summary>
    private readonly IConfiguration configuration;

    /// <summary>
    /// Morgana framework prompt loaded at construction time. Provides user-facing error message
    /// templates used when LLM calls fail or return unusable content.
    /// </summary>
    private readonly Records.Prompt morganaPrompt;

    /// <summary>
    /// Per-tier client, pricing and forced-tool capability. All three tiers are always present.
    /// </summary>
    private readonly Dictionary<Records.LLMTier, (IChatClient Client, Records.MagicDustPricing Pricing, bool CanForceToolCall)> tierClients = new();

    /// <inheritdoc/>
    public Records.LLMTier FrameworkTier { get; }

    /// <summary>
    /// Logger factory used to instrument the chat client pipeline (in particular,
    /// <see cref="OpenTelemetryChatClient"/> via <see cref="WrapWithTelemetry"/>). May be
    /// <c>null</c> in test scenarios; in that case the telemetry decorator is skipped.
    /// </summary>
    private readonly ILoggerFactory? loggerFactory;

    /// <summary>
    /// Dust limiter, wired post-construction by <see cref="EnableDustAccounting"/> from
    /// Program.cs (DI ordering: the limiter depends on persistence, registered after the LLM).
    /// Null until wired or whenever dust limiting is disabled — in which case
    /// <see cref="CompleteWithSystemPromptAsync"/> uses the bare chat client.
    /// </summary>
    private IDustLimitService? dustLimitService;

    /// <summary>
    /// Dust pricing of the <see cref="FrameworkTier"/>, set alongside <see cref="dustLimitService"/>. Used to
    /// convert its token counts into dust units for framework-actor calls.
    /// </summary>
    private Records.MagicDustPricing? dustPricing;

    /// <summary>
    /// Wires dust accounting for framework-actor LLM calls (guard, classifier, presenter,
    /// channel adapter) routed through <see cref="CompleteWithSystemPromptAsync"/>. Called
    /// once from Program.cs after both this service and <see cref="IDustLimitService"/> are
    /// constructed. Agent calls are metered separately by
    /// <see cref="Adapters.MorganaAgentAdapter"/> with a per-agent role.
    /// </summary>
    public void EnableDustAccounting(IDustLimitService dustLimitService)
    {
        this.dustLimitService = dustLimitService;

        // Guard, Classifier, Presenter and ChannelAdapter always run on the framework tier, so
        // their pricing can be fixed once here instead of looked up on every call.
        dustPricing = tierClients[FrameworkTier].Pricing;
    }

    /// <summary>
    /// Builds the client of each of the three tiers and loads the Morgana framework prompt for error message templates.
    /// </summary>
    /// <param name="configuration">Application configuration holding <c>Morgana:LLM:Tiers</c> and <c>Morgana:ActorSystem:Tier</c></param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="loggerFactory">
    /// Optional logger factory used to instrument the chat client with the MEAI OpenTelemetry
    /// decorator. Pass <c>null</c> to skip the decorator (test paths, unit tests).
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A tier is missing, carries a placeholder in a field that its provider uses or declares no positive <c>MaxOutputTokens</c>.
    /// Thrown as well when the framework tier names no tier.
    /// </exception>
    public ConfigurationLLMService(
        IConfiguration configuration,
        IPromptResolverService promptResolverService,
        ILoggerFactory? loggerFactory = null)
    {
        this.configuration = configuration;
        this.loggerFactory = loggerFactory;

        // Loads the framework prompt once at startup, so the error-message templates it
        // carries are ready before the first LLM call ever happens.
        morganaPrompt = promptResolverService.ResolveAsync(Constants.Morgana).GetAwaiter().GetResult();

        // Checked before binding: the binder's own message for a record constructor parameter
        // that finds no configuration does not name the key that the deployer has to add.
        foreach (Records.LLMTier tier in Enum.GetValues<Records.LLMTier>())
        {
            // The section of this tier is located by its key, so that a missing one can be named in the refusal.
            IConfigurationSection tierSection = configuration.GetSection($"Morgana:LLM:Tiers:{tier}");

            // The three tiers are all mandatory: an agent or a framework actor may be served by any of them.
            if (!tierSection.Exists())
                throw new InvalidOperationException($"Morgana:LLM:Tiers:{tier} is missing. All three tiers (Economy, Efficiency, Performance) must be declared.");

            // The provider decides which client the tier is built on, so a tier without one cannot be served.
            if (string.IsNullOrWhiteSpace(tierSection["Provider"]))
                throw new InvalidOperationException($"Morgana:LLM:Tiers:{tier}:Provider is missing.");

            // Left on its placeholder the binder would refuse it as an unknown provider, without saying it was never filled in.
            if (Constants.SecretOverrides.All.Contains(tierSection["Provider"]!))
                throw new InvalidOperationException(
                    $"Morgana:LLM:Tiers:{tier}:Provider is still the placeholder '{tierSection["Provider"]}'. " +
                    $"Override it via User Secrets or environment variables before starting.");

            // Checked here because a placeholder in an int field would otherwise fail inside the binder with a message that names no key.
            string? maxOutputTokens = tierSection["Options:MaxOutputTokens"];
            if (string.IsNullOrWhiteSpace(maxOutputTokens))
                throw new InvalidOperationException($"Morgana:LLM:Tiers:{tier}:Options:MaxOutputTokens is missing.");

            // A placeholder left in the ceiling would bind as nothing and let the tier run unbounded.
            if (Constants.SecretOverrides.All.Contains(maxOutputTokens))
                throw new InvalidOperationException(
                    $"Morgana:LLM:Tiers:{tier}:Options:MaxOutputTokens is still the placeholder '{maxOutputTokens}'. " +
                    $"Override it via User Secrets or environment variables before starting.");

            // The ceiling bounds what one answer may cost: zero or a sign would remove or invert the bound.
            if (!int.TryParse(maxOutputTokens, NumberStyles.None, CultureInfo.InvariantCulture, out int ceiling) || ceiling <= 0)
                throw new InvalidOperationException($"Morgana:LLM:Tiers:{tier}:Options:MaxOutputTokens must be a positive integer but is '{maxOutputTokens}'.");
        }

        // Every key is now known to be present and well formed, so the binder cannot fail on a missing one.
        Records.LLMTiers tiers = configuration.GetSection("Morgana:LLM").Get<Records.LLMConfiguration>()!.Tiers;

        // Each tier is built from its own declaration, on the provider that it names.
        foreach (Records.LLMTier tier in Enum.GetValues<Records.LLMTier>())
        {
            // The tier as the deployer declared it: provider, connection, options and pricing.
            Records.TierDefinition definition = tiers.For(tier);

            // A tier left on its placeholder would otherwise bind and build just fine, then fail
            // on its first call. This is the only point where the raw value is still visible, so a tier
            // that nobody has requested yet cannot ship broken.
            if (Constants.SecretOverrides.All.Contains(definition.Options.ModelId))
                throw new InvalidOperationException(
                    $"Morgana:LLM:Tiers:{tier}:Options:ModelId is still the placeholder '{definition.Options.ModelId}'. " +
                    $"Override it via User Secrets or environment variables before starting.");

            // Each tier is served by the provider it declares, so a deployment may mix providers across tiers.
            MorganaLanguageModel languageModel = definition.Provider switch
            {
                Records.LLMProvider.Anthropic => new LanguageModels.Anthropic(loggerFactory),
                Records.LLMProvider.AzureOpenAI => new LanguageModels.AzureOpenAI(),
                Records.LLMProvider.OpenAI => new LanguageModels.OpenAI(),
                Records.LLMProvider.Ollama => new LanguageModels.Ollama(),
                // An unknown provider name stops startup and the refusal names the tier that declared it.
                _ => throw new InvalidOperationException($"Morgana:LLM:Tiers:{tier}:Provider '{definition.Provider}' is not a known provider.")
            };

            // A connection that cannot work is refused at startup rather than at the first user turn.
            try
            {
                // The provider checks the connection it was given and a refusal is passed on with the key of the tier.
                languageModel.ValidateConnection(definition.Connection);
            }
            catch (InvalidOperationException ex)
            {
                // The provider names the field and this class knows which tier it belongs to.
                throw new InvalidOperationException($"Morgana:LLM:Tiers:{tier}:Connection:{ex.Message}", ex);
            }

            // Applied as the OUTERMOST decorator so it sees (and only fills in, never overrides) the
            // ChatOptions the caller — Microsoft.Agents.AI for domain agents, CompleteWithSystemPromptAsync
            // for framework actors — actually sent, whatever sits underneath it.
            IChatClient client = new TierDefaultsChatClient(
                WrapWithTelemetry(languageModel.CreateChatClient(definition.Connection, definition.Options)),
                definition.Options.ToChatOptions());

            // The tier is served from here on: its client, its price and whether a tool call can be forced on it.
            tierClients[tier] = (client, definition.MagicDust, languageModel.CanForceToolCall);
        }

        // The tier that serves the guard, the classifier, the presenter and the channel adapter.
        string? frameworkTierName = configuration["Morgana:ActorSystem:Tier"];

        // Left undeclared, the framework actors run on the middle tier.
        if (string.IsNullOrWhiteSpace(frameworkTierName))
            FrameworkTier = Records.LLMTier.Efficiency;

        // A tier is taken by its name in any casing. A number is not a name: it would parse to a value that serves no tier.
        else if (Enum.TryParse(frameworkTierName, ignoreCase: true, out Records.LLMTier parsedTier) && Enum.IsDefined(parsedTier))
            FrameworkTier = parsedTier;

        // Anything else names no tier, so startup stops and the refusal lists the three that exist.
        else
            throw new InvalidOperationException($"Morgana:ActorSystem:Tier '{frameworkTierName}' is not a tier. Use Economy, Efficiency or Performance.");
    }

    /// <inheritdoc/>
    public IChatClient GetChatClient(Records.LLMTier tier) => tierClients[tier].Client;

    /// <inheritdoc/>
    public bool CanForceToolCall(Records.LLMTier tier) => tierClients[tier].CanForceToolCall;

    /// <inheritdoc/>
    public Records.MagicDustPricing GetPricing(Records.LLMTier tier) => tierClients[tier].Pricing;

    /// <summary>
    /// Wraps the supplied <paramref name="innerChatClient"/> chat client with the MEAI
    /// <see cref="OpenTelemetryChatClient"/> decorator so every request emits OTel spans and
    /// metrics under the standard <c>gen_ai.*</c> semantic conventions (input/output token
    /// counts, cache_read input tokens, model name, response latency, errors).
    /// </summary>
    /// <param name="innerChatClient">The chat client to wrap (typically the raw provider client, possibly
    /// already wrapped by a provider-specific decorator like Anthropic's no-prefill guard).</param>
    /// <returns>
    /// The instrumented chat client when <see cref="loggerFactory"/> is available and
    /// <c>Morgana:OpenTelemetry:Enabled</c> is true; otherwise <paramref name="innerChatClient"/>
    /// unchanged. Provider-agnostic — every tier goes through this single hook.
    /// </returns>
    private IChatClient WrapWithTelemetry(IChatClient innerChatClient)
    {
        // Without a logger factory or with telemetry switched off the tier is served bare.
        if (loggerFactory is null || !configuration.GetValue("Morgana:OpenTelemetry:Enabled", true))
            return innerChatClient;

        // Prompt and response bodies reach the exporters only where the deployer opted in.
        bool enableSensitiveData = configuration.GetValue("Morgana:OpenTelemetry:EnableSensitiveData", false);
        return new ChatClientBuilder(innerChatClient)
            .UseOpenTelemetry(loggerFactory, Telemetry.LLMChatClientSourceName, otel => otel.EnableSensitiveData = enableSensitiveData)
            .Build();
    }

    /// <summary>
    /// Performs a completion with an explicit system prompt and user message.
    /// Primary method for actors performing stateless LLM operations (classification, guard checks).
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation (used for logging)</param>
    /// <param name="systemPrompt">System prompt defining LLM behavior</param>
    /// <param name="userPrompt">User message to process</param>
    /// <returns>
    /// LLM response text with markdown code fences removed.
    /// On error, returns user-friendly error message from Morgana prompt configuration.
    /// </returns>
    public async Task<string> CompleteWithSystemPromptAsync(string conversationId, string systemPrompt, string userPrompt)
    {
        try
        {
            // This is how Morgana's own framework actors get an LLM, as opposed to how a
            // domain agent gets one: Guard, Classifier, Presenter and ChannelAdapter all call
            // THIS method (never GetChatClient(tier)), so they always run on the tier that
            // Morgana:ActorSystem:Tier names. They have no [RequiresLLMTier] of their own;
            // a domain agent, by contrast, is built by MorganaAgentAdapter against its own
            // declared tier via GetChatClient(tier)/GetPricing(tier) and never touches this method.
            //
            // Framework-actor calls are metered under the bare framework role, which is what tells a
            // reader of the ledger that a charge belongs to the pipeline rather than to any agent.
            IChatClient frameworkChatClient = GetChatClient(FrameworkTier);
            IChatClient client = dustLimitService is not null && dustPricing is not null
                ? new DustAccountingChatClient(frameworkChatClient, dustLimitService, dustPricing, Constants.Morgana)
                : frameworkChatClient;

            // These two messages are the entire request: no history, no session, no tools and nothing
            // carried over from the caller's previous call. That is what makes this method the
            // stateless half of the LLM surface — a framework actor asks one question and is answered
            // once, where an agent's turn accumulates in a session. The conversation id travels on the
            // options so the call is attributable to the conversation that provoked it.
            ChatResponse response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt)
                ],
                new ChatOptions
                {
                    ConversationId = conversationId
                });

            // The framework actors parse JSON, which a model sometimes wraps in a markdown fence.
            return response.Text
                .Replace("```json", string.Empty)
                .Replace("```", string.Empty);
        }
        catch (Exception ex) when (ex is System.ClientModel.ClientResultException { Status: 400 } cre
                                     && cre.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase))
        {
            // A provider-level content filter rejection (e.g. Azure Prompt Shields) is not a
            // transient service error — swallowing it into the generic fallback text below would
            // hide a genuine signal from callers equipped to act on it (see LLMGuardRailService,
            // which treats this as compliant:false rather than fail-open). Classifier/Presenter/
            // ChannelAdapter have their own broad catch-all fallbacks and handle it there instead.
            throw;
        }
        catch (Exception)
        {
            // Any other failure is answered in Morgana's voice: the framework actors cannot act on a provider error.
            return morganaPrompt.GetMessage(Constants.Messages.GenericError);
        }
    }
}