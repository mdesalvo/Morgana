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
    /// prompt at construction and exposed to callers via <see cref="GetPromptResolverService"/>.
    /// </summary>
    protected readonly IPromptResolverService promptResolverService;

    /// <summary>
    /// Morgana framework prompt loaded at construction time. Provides user-facing error message
    /// templates used when LLM calls fail or return unusable content.
    /// </summary>
    protected readonly Records.Prompt morganaPrompt;

    /// <summary>
    /// Microsoft.Extensions.AI chat client for LLM interactions.
    /// Initialized by derived classes (Anthropic, AzureOpenAI, Ollama, OpenAI).
    /// </summary>
    protected IChatClient chatClient;

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
    public void EnableDustAccounting(IDustLimitService dustLimitService, Records.MagicDustPricing dustPricing)
    {
        this.dustLimitService = dustLimitService;
        this.dustPricing = dustPricing;
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

        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    /// <summary>
    /// Wraps the supplied <paramref name="inner"/> chat client with the MEAI
    /// <see cref="OpenTelemetryChatClient"/> decorator so every request emits OTel spans and
    /// metrics under the standard <c>gen_ai.*</c> semantic conventions (input/output token
    /// counts, cache_read input tokens, model name, response latency, errors).
    /// </summary>
    /// <param name="inner">The chat client to wrap (typically the raw provider client, possibly
    /// already wrapped by a provider-specific decorator like Anthropic's no-prefill guard).</param>
    /// <returns>
    /// The instrumented chat client when <see cref="loggerFactory"/> is available and
    /// <c>Morgana:OpenTelemetry:Enabled</c> is true; otherwise <paramref name="inner"/>
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
    protected IChatClient WrapWithTelemetry(IChatClient inner)
    {
        if (loggerFactory is null)
            return inner;
        if (!configuration.GetValue("Morgana:OpenTelemetry:Enabled", true))
            return inner;

        bool enableSensitiveData = configuration.GetValue("Morgana:OpenTelemetry:EnableSensitiveData", false);

        return new ChatClientBuilder(inner)
            .UseOpenTelemetry(loggerFactory, MorganaTelemetry.LLMChatClientSourceName,
                otel => otel.EnableSensitiveData = enableSensitiveData)
            .Build();
    }

    /// <summary>
    /// Gets the underlying Microsoft.Extensions.AI chat client.
    /// Used by MorganaAgentAdapter to create AIAgent instances with tool calling support.
    /// </summary>
    /// <returns>IChatClient instance configured for the active provider</returns>
    public IChatClient GetChatClient() => chatClient;

    /// <summary>
    /// Gets the prompt resolver service associated with this LLM service.
    /// </summary>
    /// <returns>IPromptResolverService instance</returns>
    public IPromptResolverService GetPromptResolverService() => promptResolverService;

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
            // Framework-actor calls (guard, classifier, presenter, channel adapter) are
            // metered under the role "Morgana". Wrapping is per-call: no singleton dust
            // wrapper exists on chatClient, so this never double-counts the agent path.
            IChatClient client = dustLimitService is not null && dustPricing is not null
                ? new DustAccountingChatClient(chatClient, dustLimitService, dustPricing, "Morgana")
                : chatClient;

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