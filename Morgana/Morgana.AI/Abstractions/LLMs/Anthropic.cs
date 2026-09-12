using System.Diagnostics;
using System.Net;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.ChatClients;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions.LLMs;

/// <summary>
/// Anthropic implementation of ILLMService.<br/>
/// Supports Claude models (claude-fable-5, claude-sonnet-5, ...)
/// </summary>
/// <remarks>
/// Anthropic provider implementation. Configuration under Morgana:LLM:Anthropic with ApiKey
/// and Tiers map (Efficiency/Performance). MagicDust pricing defaults are calibrated to the
/// claude-haiku-4-5 and claude-sonnet-5 model pair. If you point either tier to a different
/// model, recalibrate InputTokensPerDustUnit and OutputTokensPerDustUnit per that model's
/// actual published per-token pricing.
/// </remarks>
public class Anthropic : MorganaLLM
{
    /// <summary>
    /// Initializes a new instance of Anthropic.
    /// Creates Anthropic client and wraps it with Microsoft.Extensions.AI IChatClient,
    /// then with the in-process <see cref="MorganaAnthropicClient"/> that enforces Claude 4.6+
    /// no-prefill constraint at the API boundary.
    /// </summary>
    /// <param name="configuration">Application configuration containing Anthropic API key and model.</param>
    /// <param name="promptResolverService">Service for resolving prompt templates.</param>
    /// <param name="loggerFactory">
    /// Optional logger factory used by <see cref="MorganaAnthropicClient"/>. When <c>null</c>, the guard's
    /// diagnostic channel is silent but the message-list normalization still applies.
    /// </param>
    public Anthropic(
        IConfiguration configuration,
        IPromptResolverService promptResolverService,
        ILoggerFactory? loggerFactory = null) : base(configuration, promptResolverService, loggerFactory)
    {
        // A single low-level AnthropicClient (API key only, no model) is enough — the SDK binds
        // the model at the IChatClient adapter layer below, not here, so this one client is
        // shared across every tier.
        // Left to its defaults the SDK retries a rate-limited or overloaded call on its own, with a
        // backoff nobody sees and no ceiling anybody chose: a turn that is being throttled is
        // indistinguishable from a turn that is thinking, for minutes, and the caller waits it out
        // with nothing on the screen and nothing in the log. The timeout bounds ONE attempt, so the
        // worst case of a call is it times the retries — which is what any ceiling above has to be
        // read against, since a caller's own budget covers a whole agentic loop of such calls.
        AnthropicClient anthropicClient = new AnthropicClient(
            new ClientOptions
            {
                ApiKey = this.configuration["Morgana:LLM:Anthropic:ApiKey"]!,
                MaxRetries = this.configuration.GetValue("Morgana:LLM:Anthropic:MaxRetries", 2),
                Timeout = TimeSpan.FromSeconds(this.configuration.GetValue("Morgana:LLM:Anthropic:TimeoutSeconds", 60)),
                Handlers = [new AttemptLogger(loggerFactory?.CreateLogger<Anthropic>())]
            });

        // Binds the tiers declared in configuration so they're available at runtime for
        // matching against each agent's declared tier (see Records.TierDefinition remarks
        // for how the config layout is structured).
        Dictionary<Records.LLMTier, Records.TierDefinition> tiers =
            this.configuration.GetSection("Morgana:LLM:Anthropic:Tiers").Get<Dictionary<Records.LLMTier, Records.TierDefinition>>() ?? [];

        // One IChatClient per configured tier, all sharing the same underlying AnthropicClient
        // (API key only) but each bound to its own model name. Decorator chain (innermost →
        // outermost), applied per tier:
        //   1. AnthropicClient.AsIChatClient(tier)    — raw SDK adapter
        //   2. MorganaAnthropicClient                — Anthropic-specific: no-prefill guard +
        //                                              prompt-cache marker on leading system
        //   3. WrapWithTelemetry (MorganaLLM)        — MEAI OpenTelemetryChatClient: gen_ai.*
        //                                              spans/metrics, including cache_read.input_tokens
        foreach ((Records.LLMTier tier, Records.TierDefinition tierDefinition) in tiers)
        {
            IChatClient tierClient = WrapWithTelemetry(
                new MorganaAnthropicClient(anthropicClient.AsIChatClient(tierDefinition.Options.ModelId), loggerFactory));

            RegisterTierClient(tier, tierDefinition.Options.ModelId, tierClient, tierDefinition.MagicDust, tierDefinition.Options.ToChatOptions());
        }

        // Wraps up tier registration and picks which client the framework's own actors
        // (Guard, Classifier, Presenter, ChannelAdapter) will use.
        FinalizeModelRegistration();
    }

    /// <summary>
    /// Writes down every call actually put on the wire, including the ones the SDK retries by itself.
    /// </summary>
    /// <remarks>
    /// The telemetry wrapper above measures a completion end to end, so a call throttled three times
    /// and answered on the fourth reads there as one slow call. This is the only place that can say
    /// it was throttled: a refusal carrying a retry-after is recorded as a refusal, so a wait nobody
    /// asked for stops looking like a model taking its time.
    /// </remarks>
    private sealed class AttemptLogger(ILogger? logger) : DelegatingHandler
    {
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            long startedAt = Stopwatch.GetTimestamp();

            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

            double elapsed = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;

            if (response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError)
                logger?.LogWarning(
                    "Anthropic refused a call with {Status} after {Elapsed:0.0}s and asks to wait {RetryAfter}s; the SDK will retry it",
                    (int)response.StatusCode, elapsed,
                    response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0") ?? "an unstated number of");
            else
                logger?.LogInformation("Anthropic answered {Status} in {Elapsed:0.0}s", (int)response.StatusCode, elapsed);

            return response;
        }
    }
}