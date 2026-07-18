using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using Morgana.AI.Telemetry;

namespace Morgana.AI.Abstractions;

/// <summary>
/// Base implementation of ILLMService providing common LLM interaction patterns.
/// Supports multiple LLM providers (Anthropic, Azure OpenAI, Ollama, OpenAI) via the
/// Microsoft.Extensions.AI abstraction.
/// </summary>
/// <remarks>
/// <para><strong>Architecture:</strong></para>
/// <code>
/// MorganaLLM (abstract base, this file)
///   ├── Abstractions/LLMs/
///   │     ├── Anthropic.cs    (Anthropic Claude models)
///   │     ├── AzureOpenAI.cs  (Azure OpenAI GPT models)
///   │     ├── Ollama.cs       (Ollama local models)
///   │     └── OpenAI.cs       (OpenAI GPT models)
///   └── Abstractions/ChatClients/
///         ├── TierDefaultsChatClient.cs   (per-tier ChatOptions defaults)
///         ├── DustAccountingChatClient.cs (Magic Dust metering)
///         └── MorganaAnthropicClient.cs   (Anthropic no-prefill guard + cache marker)
/// </code>
/// <para><strong>Key Features:</strong></para>
/// <list type="bullet">
/// <item><term>Conversation Management</term><description>Tracks conversation history per conversationId</description></item>
/// <item><term>Error Handling</term><description>Returns user-friendly error messages from Morgana prompts</description></item>
/// <item><term>JSON Cleanup</term><description>Strips markdown code fences from LLM responses</description></item>
/// <item><term>System Prompt Support</term><description>Explicit system prompt injection for actors</description></item>
/// </list>
/// </remarks>
public class MorganaLLM : ILLMService
{
    /// <summary>
    /// Application configuration used by derived classes to read provider-specific settings
    /// (API keys, endpoints, model names, deployment IDs).
    /// </summary>
    protected readonly IConfiguration configuration;

    /// <summary>
    /// Service for resolving prompt templates by name. Used to load the Morgana framework
    /// prompt at construction.
    /// </summary>
    protected readonly IPromptResolverService promptResolverService;

    /// <summary>
    /// Morgana framework prompt loaded at construction time. Provides user-facing error message
    /// templates used when LLM calls fail or return unusable content.
    /// </summary>
    protected readonly Records.Prompt morganaPrompt;

    /// <summary>
    /// Chat client used by Morgana's own framework actors (Guard, Classifier, Presenter,
    /// ChannelAdapter) — always the cheapest tier the active provider has configured, set by
    /// <see cref="FinalizeModelRegistration"/>, which every provider constructor is required
    /// to call (hence the <c>null!</c> initializer: the field is guaranteed assigned before
    /// any instance is ever handed out by DI). Domain agents never read this field; they
    /// resolve their own tier via <see cref="GetChatClient(Records.LLMTier)"/>.
    /// </summary>
    private IChatClient frameworkChatClient = null!;

    /// <summary>
    /// Per-tier client + pricing, populated by derived class constructors via
    /// <see cref="RegisterTierClient"/> — one entry per tier key configured under
    /// <c>Tiers</c> for the active provider.
    /// </summary>
    private readonly Dictionary<Records.LLMTier, (IChatClient Client, Records.MagicDustPricing Pricing)> tierClients = new();

    /// <summary>
    /// The tier used for framework-actor calls (Guard, Classifier, Presenter, ChannelAdapter).
    /// Always the cheapest tier the active provider has configured. Computed lazily from
    /// <see cref="tierClients"/> once <see cref="FinalizeModelRegistration"/> has run.
    /// </summary>
    private Records.LLMTier FrameworkDefaultTier => tierClients.Keys.Min();

    /// <summary>
    /// Logger factory used to instrument the chat client pipeline (in particular,
    /// <see cref="OpenTelemetryChatClient"/> via <see cref="WrapWithTelemetry"/>). May be
    /// <c>null</c> in test scenarios; in that case the telemetry decorator is skipped.
    /// </summary>
    protected readonly ILoggerFactory? loggerFactory;

    /// <summary>
    /// Dust limiter, wired post-construction by <see cref="EnableDustAccounting"/> from
    /// Program.cs (DI ordering: the limiter depends on persistence, registered after the LLM).
    /// Null until wired, or whenever dust limiting is disabled — in which case
    /// <see cref="CompleteWithSystemPromptAsync"/> uses the bare chat client.
    /// </summary>
    private IDustLimitService? dustLimitService;

    /// <summary>
    /// Per-provider dust pricing, set alongside <see cref="dustLimitService"/>. Used to
    /// convert this provider's token counts into dust units for framework-actor calls.
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

        // Guard, Classifier, Presenter and ChannelAdapter always run on the cheapest tier, so
        // their pricing can be fixed once here instead of looked up on every call.
        dustPricing = tierClients[FrameworkDefaultTier].Pricing;
    }

    /// <summary>
    /// Initializes MorganaLLM abstraction.
    /// Loads the Morgana framework prompt for error message templates.
    /// </summary>
    /// <param name="configuration">Application configuration for provider-specific settings</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="loggerFactory">
    /// Optional logger factory used to instrument the chat client with the MEAI OpenTelemetry
    /// decorator. Pass <c>null</c> to skip the decorator (test paths, unit tests).
    /// </param>
    public MorganaLLM(
        IConfiguration configuration,
        IPromptResolverService promptResolverService,
        ILoggerFactory? loggerFactory = null)
    {
        this.configuration = configuration;
        this.promptResolverService = promptResolverService;
        this.loggerFactory = loggerFactory;

        // Loads the framework prompt once at startup, so the error-message templates it
        // carries are ready before the first LLM call ever happens.
        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    /// <summary>
    /// Registers a fully-built chat client for one <c>Tiers</c> entry. Called once per
    /// entry by each derived provider's constructor, after applying that provider's own
    /// decorator chain (no-prefill guard, telemetry, etc.) to the model-specific client.
    /// </summary>
    /// <param name="tier">Tier this model serves — the key it was registered under in the <c>Tiers</c> map.</param>
    /// <param name="modelName">
    /// The model/deployment identifier this tier resolves to (<c>TierDefinition.Options.ModelId</c>),
    /// checked against <see cref="Records.OverridePlaceholders"/> before registration. Not
    /// stored — the client already has it baked in — this parameter exists purely so every
    /// provider gets the check for free instead of each duplicating it.
    /// </param>
    /// <param name="client">Fully decorated chat client for this specific tier.</param>
    /// <param name="pricing">Dust pricing for this specific tier.</param>
    /// <param name="tierDefaultOptions">
    /// Materialized tier defaults (<see cref="Records.TierConfiguration.ToChatOptions"/>), applied
    /// field-by-field — fill-if-absent, never overriding a value the caller already set — to
    /// every call through the registered client. See <see cref="TierDefaultsChatClient"/>.
    /// Pass <c>null</c> to register the client with no tier-level defaults at all.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="modelName"/> is still an unreplaced override placeholder.
    /// Deliberately narrow: it only catches the exact sentinel strings, not "implausible"
    /// values in general (empty, whitespace, garbage) — those remain the deployer's problem,
    /// surfacing as an HTTP error from the provider on first real call.
    /// </exception>
    protected void RegisterTierClient(Records.LLMTier tier, string modelName, IChatClient client, Records.MagicDustPricing pricing, ChatOptions? tierDefaultOptions = null)
    {
        // A tier left on its placeholder would otherwise bind and register just fine, then
        // pass every later check (ConfiguredTiers reports it as present, so
        // RequiresLLMTierValidationService's "tier exists" check sees nothing wrong) — this is
        // the only point left where the raw, still-a-placeholder value is visible before it
        // disappears into an already-built client.
        if (Records.OverridePlaceholders.Contains(modelName))
            throw new InvalidOperationException(
                $"Morgana:LLM:{{Provider}}:Tiers:{tier} has a ModelId that is still the " +
                $"placeholder '{modelName}'. Override it via User Secrets or environment variables before " +
                $"starting — this is checked now, at provider startup, so a tier nobody has requested yet " +
                $"(e.g. one only a plugin discovered later will use) can't silently ship broken.");

        // Applied as the OUTERMOST decorator so it sees (and only fills in, never overrides) the
        // ChatOptions the caller — Microsoft.Agents.AI for domain agents, CompleteWithSystemPromptAsync
        // for framework actors — actually sent, regardless of which provider-specific decorators
        // (no-prefill guard, telemetry, ...) sit underneath.
        if (tierDefaultOptions is not null)
            client = new TierDefaultsChatClient(client, tierDefaultOptions);

        tierClients[tier] = (client, pricing);
    }

    /// <summary>
    /// Finalizes tier registration: picks the cheapest configured tier as the
    /// <see cref="frameworkChatClient"/>. Must be called by every derived provider's
    /// constructor after all <see cref="RegisterTierClient"/> calls for its <c>Tiers</c> map.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a provider declares no <c>Tiers</c> entries at all (an agent could never
    /// resolve any tier against it).
    /// </exception>
    protected void FinalizeModelRegistration()
    {
        if (tierClients.Count == 0)
            throw new InvalidOperationException(
                "No tiers configured under 'Morgana:LLM:{Provider}:Tiers'. At least one tier " +
                "(keyed by Efficiency/Performance, with a ModelId and MagicDust pricing) is required for the active LLM provider.");

        // The framework's own actors (Guard, Classifier, Presenter, ChannelAdapter) always run
        // on the cheapest tier available, whatever that turns out to be for this provider.
        frameworkChatClient = tierClients[FrameworkDefaultTier].Client;
    }

    /// <summary>
    /// Resolves the tier client/pricing entry to actually serve for <paramref name="tier"/>: an
    /// exact match is required — an agent asking for a tier the deployment never configured is
    /// a misconfiguration, not something to paper over with a fallback.
    /// </summary>
    private (IChatClient Client, Records.MagicDustPricing Pricing) ResolveTierEntry(Records.LLMTier tier) =>
        tierClients.TryGetValue(tier, out (IChatClient Client, Records.MagicDustPricing Pricing) entry)
            ? entry
            : throw new InvalidOperationException(
                $"LLM tier '{tier}' is not configured for the active provider. Add a \"{tier}\" entry " +
                $"under Morgana:LLM:{{Provider}}:Tiers.");

    /// <inheritdoc/>
    public IReadOnlyCollection<Records.LLMTier> ConfiguredTiers => tierClients.Keys;

    /// <inheritdoc/>
    public IChatClient GetChatClient(Records.LLMTier tier) => ResolveTierEntry(tier).Client;

    /// <inheritdoc/>
    public Records.MagicDustPricing GetPricing(Records.LLMTier tier) => ResolveTierEntry(tier).Pricing;

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
    /// unchanged. Provider-agnostic — all four concrete providers go through this single hook.
    /// </returns>
    /// <remarks>
    /// <para>The activity source / meter name is fixed (<c>Morgana.AI.LLM</c>) so the OTel
    /// pipeline registration is centralised; per-provider differentiation comes from the
    /// <c>gen_ai.system</c> attribute that the MEAI decorator emits automatically.</para>
    /// <para><c>EnableSensitiveData</c> is read from <c>Morgana:OpenTelemetry:EnableSensitiveData</c>
    /// (default <c>false</c>): when true, the spans include the actual message contents — useful
    /// in dev/troubleshooting, off in production for privacy.</para>
    /// </remarks>
    protected IChatClient WrapWithTelemetry(IChatClient innerChatClient)
    {
        if (loggerFactory is null || !configuration.GetValue("Morgana:OpenTelemetry:Enabled", true))
            return innerChatClient;

        bool enableSensitiveData = configuration.GetValue("Morgana:OpenTelemetry:EnableSensitiveData", false);
        return new ChatClientBuilder(innerChatClient)
            .UseOpenTelemetry(loggerFactory, MorganaTelemetry.LLMChatClientSourceName, otel => otel.EnableSensitiveData = enableSensitiveData)
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
            // THIS method (never GetChatClient(tier)), so they always run on
            // frameworkChatClient — the cheapest tier the active provider has configured,
            // fixed once at startup by FinalizeModelRegistration. They have no
            // [RequiresLLMTier] of their own; a domain agent, by contrast, is built by
            // MorganaAgentAdapter against its own declared tier via
            // GetChatClient(tier)/GetPricing(tier), and never touches this method or
            // frameworkChatClient at all.
            //
            // Framework-actor calls are metered under the role "Morgana"
            IChatClient client = dustLimitService is not null && dustPricing is not null
                ? new DustAccountingChatClient(frameworkChatClient, dustLimitService, dustPricing, "Morgana")
                : frameworkChatClient;

            ChatResponse response = await client.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt)
                ],
                new ChatOptions
                {
                    ConversationId = conversationId
                });

            // Strip markdown code fences from JSON responses
            return response.Text
                .Replace("```json", string.Empty)
                .Replace("```", string.Empty);
        }
        catch (Exception ex)
        {
            // Return user-friendly error message from Morgana prompts
            List<Records.ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<Records.ErrorAnswer>>("ErrorAnswers");
            Records.ErrorAnswer? llmError = errorAnswers.FirstOrDefault(e => string.Equals(e.Name, "LLMServiceError", StringComparison.OrdinalIgnoreCase));

            return llmError?.Content.Replace("((llm_error))", ex.Message)
                          ?? $"LLM service error: {ex.Message}";
        }
    }
}