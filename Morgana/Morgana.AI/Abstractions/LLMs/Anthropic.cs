using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions.LLMs;

/// <summary>
/// Anthropic implementation of ILLMService.<br/>
/// Supports Claude models (claude-fable-5, claude-sonnet-5, ...)
/// </summary>
/// <remarks>
/// <para><strong>Configuration (appsettings.json):</strong></para>
/// <code>
/// {
///   "Morgana": {
///     "LLM": {
///       "Provider": "anthropic",
///       "Anthropic": {
///         "ApiKey": "sk-ant-...",
///         "Tiers": {
///           "Efficiency": { "Options": { "ModelId": "claude-haiku-4-5", "MaxOutputTokens": 4096 }, "MagicDust": { ... } },
///           "Performance": { "Options": { "ModelId": "claude-sonnet-5", "MaxOutputTokens": 8192 }, "MagicDust": { ... } }
///         }
///       }
///     }
///   }
/// }
/// </code>
/// <para><strong>MagicDust defaults are calibrated to the claude-haiku-4-5/claude-sonnet-5 pair
/// shown above — recalibrate if you point a tier at a different model</strong> (e.g. Opus-tier
/// on <c>Performance</c>), since <c>InputTokensPerDustUnit</c>/<c>OutputTokensPerDustUnit</c>
/// are derived from that specific model's published per-token pricing, not a property of the
/// tier name itself.</para>
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
        AnthropicClient anthropicClient = new AnthropicClient(
            new ClientOptions
            {
                ApiKey = this.configuration["Morgana:LLM:Anthropic:ApiKey"]!
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
}