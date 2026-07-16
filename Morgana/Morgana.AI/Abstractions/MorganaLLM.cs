using System.Runtime.CompilerServices;
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
///   └── Abstractions/LLMs/
///         ├── Anthropic.cs    (Anthropic Claude models)
///         ├── AzureOpenAI.cs  (Azure OpenAI GPT models)
///         ├── Ollama.cs       (Ollama local models)
///         └── OpenAI.cs       (OpenAI GPT models)
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
    /// <c>Models</c> for the active provider.
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
    /// Registers a fully-built chat client for one <c>Models</c> entry. Called once per
    /// entry by each derived provider's constructor, after applying that provider's own
    /// decorator chain (no-prefill guard, telemetry, etc.) to the model-specific client.
    /// </summary>
    /// <param name="tier">Tier this model serves — the key it was registered under in the <c>Models</c> map.</param>
    /// <param name="modelName">
    /// The model/deployment identifier this tier resolves to (<c>ModelDefinition.Name</c>),
    /// checked against <see cref="Records.OverridePlaceholders"/> before registration. Not
    /// stored — the client already has it baked in — this parameter exists purely so every
    /// provider gets the check for free instead of each duplicating it.
    /// </param>
    /// <param name="client">Fully decorated chat client for this specific model.</param>
    /// <param name="pricing">Dust pricing for this specific model.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="modelName"/> is still an unreplaced override placeholder.
    /// Deliberately narrow: it only catches the exact sentinel strings, not "implausible"
    /// values in general (empty, whitespace, garbage) — those remain the deployer's problem,
    /// surfacing as an HTTP error from the provider on first real call.
    /// </exception>
    protected void RegisterTierClient(Records.LLMTier tier, string modelName, IChatClient client, Records.MagicDustPricing pricing)
    {
        // A tier left on its placeholder would otherwise bind and register just fine, then
        // pass every later check (ConfiguredTiers reports it as present, so
        // RequiresLLMTierValidationService's "tier exists" check sees nothing wrong) — this is
        // the only point left where the raw, still-a-placeholder value is visible before it
        // disappears into an already-built client.
        if (Records.OverridePlaceholders.Contains(modelName))
            throw new InvalidOperationException(
                $"Morgana:LLM:{{Provider}}:Models:{tier} has a Name that is still the " +
                $"placeholder '{modelName}'. Override it via User Secrets or environment variables before " +
                $"starting — this is checked now, at provider startup, so a tier nobody has requested yet " +
                $"(e.g. one only a plugin discovered later will use) can't silently ship broken.");

        tierClients[tier] = (client, pricing);
    }

    /// <summary>
    /// Finalizes tier registration: picks the cheapest configured tier as the
    /// <see cref="frameworkChatClient"/>. Must be called by every derived provider's
    /// constructor after all <see cref="RegisterTierClient"/> calls for its <c>Models</c> map.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when a provider declares no <c>Models</c> entries at all (an agent could never
    /// resolve any tier against it), or when <see cref="Records.LLMTier.Omni"/> is mixed with
    /// any other tier (ambiguous — Omni is meant to be the sole entry for that provider).
    /// </exception>
    protected void FinalizeModelRegistration()
    {
        if (tierClients.Count == 0)
            throw new InvalidOperationException(
                "No models configured under 'Morgana:LLM:{Provider}:Models'. At least one model " +
                "(keyed by Tier, with a Name and MagicDust pricing) is required for the active LLM provider.");

        // A deployment that configures Omni AND Low/Moderate/High doesn't know what it wants:
        // should an agent asking for Low get the explicit Low model, or the Omni catch-all?
        // Refuse to guess.
        if (tierClients.ContainsKey(Records.LLMTier.Omni) && tierClients.Count > 1)
            throw new InvalidOperationException(
                "'Morgana:LLM:{Provider}:Models' mixes an Omni entry with other tiers. Omni means " +
                "\"this one model serves every tier\" and must be the sole entry — either configure " +
                "Omni alone, or drop it and configure Low/Moderate/High explicitly.");

        // The framework's own actors (Guard, Classifier, Presenter, ChannelAdapter) always run
        // on the cheapest tier available, whatever that turns out to be for this provider.
        frameworkChatClient = tierClients[FrameworkDefaultTier].Client;
    }

    /// <summary>
    /// Resolves the tier client/pricing entry to actually serve for <paramref name="tier"/>:
    /// if the provider is configured Omni-only, EVERY request — regardless of the tier asked
    /// for — is transparently redirected to that single entry (see <see cref="Records.LLMTier.Omni"/>).
    /// Otherwise, an exact match is required.
    /// </summary>
    private (IChatClient Client, Records.MagicDustPricing Pricing) ResolveTierEntry(Records.LLMTier tier)
    {
        // On an Omni deployment, every request lands on the same single model, whatever tier
        // was actually asked for.
        if (tierClients.TryGetValue(Records.LLMTier.Omni, out (IChatClient Client, Records.MagicDustPricing Pricing) omniEntry))
            return omniEntry;

        // Otherwise the requested tier must exist exactly as declared — an agent asking for a
        // tier the deployment never configured is a misconfiguration, not something to paper
        // over with a fallback.
        return tierClients.TryGetValue(tier, out (IChatClient Client, Records.MagicDustPricing Pricing) entry)
            ? entry
            : throw new InvalidOperationException(
                $"LLM tier '{tier}' is not configured for the active provider. Add a \"{tier}\" entry " +
                $"under Morgana:LLM:{{Provider}}:Models (or a single \"Omni\" entry to serve every tier).");
    }

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

/// <summary>
/// A <see cref="DelegatingChatClient"/> that meters token consumption ("magic dust") for
/// every LLM call passing through it and charges the conversation's lifetime budget.
/// </summary>
/// <remarks>
/// <para><strong>Why an instance carries its own llmRole.</strong> There is no singleton dust
/// wrapper on <see cref="MorganaLLM"/>'s chat client. Instead this client is constructed at
/// exactly two points, each stamping its own <c>llmRole</c> at construction time — so we never
/// need a separate llmRole-stamping decorator, and the same call is never charged twice:</para>
/// <list type="bullet">
///   <item><see cref="MorganaLLM.CompleteWithSystemPromptAsync"/> wraps per call with llmRole
///   <c>"Morgana"</c> (framework actors: guard, classifier, presenter, channel adapter).</item>
///   <item><see cref="Adapters.MorganaAgentAdapter"/> wraps per agent with llmRole
///   <c>"Morgana (Intent)"</c> (domain agents and their history reducer).</item>
/// </list>
/// <para>The conversation id is read from <see cref="ChatOptions.ConversationId"/>, which
/// every caller already sets. Charging is best-effort: <see cref="IDustLimitService"/> itself
/// fails open, and any exception here is swallowed so dust accounting can never break a turn.</para>
/// </remarks>
public sealed class DustAccountingChatClient : DelegatingChatClient
{
    private readonly IDustLimitService dustLimitService;
    private readonly Records.MagicDustPricing dustPricing;
    private readonly string llmRole;
    private readonly string? conversationId;

    /// <summary>
    /// Wraps <paramref name="innerClient"/>, metering every call against
    /// <paramref name="dustLimitService"/> using <paramref name="dustPricing"/> and attributing
    /// the cost to <paramref name="llmRole"/>.
    /// </summary>
    public DustAccountingChatClient(
        IChatClient innerClient,
        IDustLimitService dustLimitService,
        Records.MagicDustPricing dustPricing,
        string llmRole,
        string? conversationId = null) : base(innerClient)
    {
        this.dustLimitService = dustLimitService;
        this.dustPricing = dustPricing;
        this.llmRole = llmRole;
        this.conversationId = conversationId;
    }

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
    {
        ChatResponse chatResponse = await base.GetResponseAsync(chatMessages, chatOptions, cancellationToken);
        await ChargeAsync(ResolveConversationId(chatOptions), chatResponse.Usage);
        return chatResponse;
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Providers deliver streaming usage as a cumulative total (typically one final
        // UsageContent), not per-chunk deltas — so we keep the LAST one seen and charge once
        // when the stream completes. Summing would double-count.
        UsageDetails? usageDetails = null;

        await foreach (ChatResponseUpdate chatResponseUpdate in
            base.GetStreamingResponseAsync(chatMessages, chatOptions, cancellationToken))
        {
            foreach (UsageContent usageContent in chatResponseUpdate.Contents.OfType<UsageContent>())
                usageDetails = usageContent.Details;

            yield return chatResponseUpdate;
        }

        await ChargeAsync(ResolveConversationId(chatOptions), usageDetails);
    }

    /// <summary>
    /// Prefers the conversation id carried on the call's <see cref="ChatOptions"/> (the
    /// framework-actor path sets it); falls back to the id baked in at construction (the
    /// agent path, where Microsoft.Agents.AI does not flow a conversation id).
    /// </summary>
    private string? ResolveConversationId(ChatOptions? chatOptions) =>
        string.IsNullOrEmpty(chatOptions?.ConversationId) ? conversationId : chatOptions.ConversationId;

    /// <summary>
    /// Converts a usageDetails report into dust via the per-provider dustPricing and charges it.
    /// No-op when the conversation id or usageDetails is absent, or when the computed dust is zero
    /// (e.g. Ollama priced at 0 tokens-per-unit on both axes). Never throws.
    /// </summary>
    /// <remarks>
    /// The Anthropic MEAI adapter reports <see cref="UsageDetails.InputTokenCount"/> as the
    /// total prompt (fresh + cache-read + cache-write), with cache-read in
    /// <see cref="UsageDetails.CachedInputTokenCount"/> and cache-write in
    /// <c>AdditionalCounts["CacheCreationInputTokens"]</c>. We decompose it and apply the
    /// per-provider cache weights so the charge tracks real cache economics rather than
    /// over-counting cheap cache reads at full price.
    /// </remarks>
    private async Task ChargeAsync(string? convId, UsageDetails? usageDetails)
    {
        if (string.IsNullOrEmpty(convId) || usageDetails is null)
            return;

        long totalInput = usageDetails.InputTokenCount ?? 0;
        long cacheRead = usageDetails.CachedInputTokenCount ?? 0;

        long cacheWrite = 0;
        if (usageDetails.AdditionalCounts is not null &&
            usageDetails.AdditionalCounts.TryGetValue("CacheCreationInputTokens", out long w))
            cacheWrite = w;

        // Fresh = total minus the two cache components. Clamp at 0: defends against any
        // adapter that might report the components non-disjointly.
        long freshInput = Math.Max(0, totalInput - cacheRead - cacheWrite);

        double effectiveInput =
            freshInput +
            cacheRead * dustPricing.CachedInputWeight +
            cacheWrite * dustPricing.CacheCreationWeight;

        long outputTokens = usageDetails.OutputTokenCount ?? 0;

        double dust =
            (dustPricing.InputTokensPerDustUnit > 0
                ? effectiveInput / dustPricing.InputTokensPerDustUnit
                : 0.0) +
            (dustPricing.OutputTokensPerDustUnit > 0
                ? (double)outputTokens / dustPricing.OutputTokensPerDustUnit
                : 0.0);

        if (dust <= 0.0)
            return;

        try
        {
            await dustLimitService.ChargeAsync(convId, dust, llmRole);
        }
        catch
        {
            // Dust accounting must never break a turn. The service itself already fails open;
            // this is the last-resort belt-and-braces for anything it might surface.
        }
    }
}