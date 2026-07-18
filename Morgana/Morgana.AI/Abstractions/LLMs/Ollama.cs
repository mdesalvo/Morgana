using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using OllamaSharp;

namespace Morgana.AI.Abstractions.LLMs;

/// <summary>
/// Ollama implementation of ILLMService.<br/>
/// Supports local models via Ollama interface (gpt-oss:20b, phi4-mini ...).
/// </summary>
/// <remarks>
/// <para><strong>Configuration (appsettings.json):</strong></para>
/// <code>
/// {
///   "Morgana": {
///     "LLM": {
///       "Provider": "ollama",
///       "Ollama": {
///         "Endpoint": "http://localhost:11434/",
///         "Tiers": {
///           "Efficiency": { "Options": { "ModelId": "phi4-mini" }, "MagicDust": { "InputTokensPerDustUnit": 0, "OutputTokensPerDustUnit": 0, "CachedInputWeight": 1.0, "CacheCreationWeight": 1.0 } }
///         }
///       }
///     }
///   }
/// }
/// </code>
/// <para><strong>Important Notes:</strong></para>
/// <para>- Morgana is an AI orchestrator which relies heavily on tool calling (context variables, quick replies, rich cards).
/// For best result, please choose a model with solid function calling support (e.g: gpt-oss:20b, phi4-mini).</para>
/// <para>- Before starting Morgana, check with "ollama ps" that your model is already loaded into memory!</para>
/// <para><strong>On tiers:</strong> unlike the cloud providers, each configured tier here is a
/// physically distinct local model that must fit in your machine's RAM/VRAM — there is no
/// "just call a different API" shortcut. The typical single-model dev setup declares only the
/// <see cref="Records.LLMTier.Efficiency"/> entry (as shown above): every routine agent and
/// Morgana's own framework actors resolve against it. An agent authored against
/// <see cref="Records.LLMTier.Performance"/> fails startup against this config — deliberately,
/// there is no cross-tier fallback (see <see cref="Attributes.RequiresLLMTierAttribute"/>
/// remarks) — so add a real <c>Performance</c> entry too only if you actually keep two distinct
/// models (E-core + P-core) loaded.</para>
/// </remarks>
public class Ollama : MorganaLLM
{
    /// <summary>
    /// Initializes a new instance of Ollama.
    /// Creates Ollama client and wraps it with Microsoft.Extensions.AI IChatClient.
    /// </summary>
    /// <param name="configuration">Application configuration containing Ollama endpoint and model</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="loggerFactory">Optional logger factory used to instrument the chat client with the MEAI OpenTelemetry decorator.</param>
    public Ollama(
        IConfiguration configuration,
        IPromptResolverService promptResolverService,
        ILoggerFactory? loggerFactory = null) : base(configuration, promptResolverService, loggerFactory)
    {
        // Binds the tiers declared in configuration so they're available at runtime for
        // matching against each agent's declared tier (see Records.TierDefinition remarks
        // for how the config layout is structured). In practice most Ollama deployments only
        // ever populate the single "Efficiency" key (see class remarks on tiers).
        Dictionary<Records.LLMTier, Records.TierDefinition> tiers =
            this.configuration.GetSection("Morgana:LLM:Ollama:Tiers").Get<Dictionary<Records.LLMTier, Records.TierDefinition>>() ?? [];

        Uri endpoint = new Uri(this.configuration["Morgana:LLM:Ollama:Endpoint"]!);
        TimeSpan timeout = TimeSpan.FromSeconds(Convert.ToInt32(this.configuration["Morgana:ActorSystem:TimeoutSeconds"]));

        // Ollama's client binds its model at construction (unlike the SDK-based providers,
        // there is no single client + per-call model selection), so one OllamaApiClient per
        // configured tier — each with its own HttpClient, since HttpClient.BaseAddress is
        // fixed but the model differs, and OllamaApiClient does not expose overriding it later.
        foreach ((Records.LLMTier tier, Records.TierDefinition tierDefinition) in tiers)
        {
            IChatClient tierClient = WrapWithTelemetry(
                new OllamaApiClient(
                    new HttpClient { BaseAddress = endpoint, Timeout = timeout },
                    tierDefinition.Options.ModelId));
            RegisterTierClient(tier, tierDefinition.Options.ModelId, tierClient, tierDefinition.MagicDust, tierDefinition.Options.ToChatOptions());
        }

        // Wraps up tier registration and picks which client the framework's own actors
        // (Guard, Classifier, Presenter, ChannelAdapter) will use.
        FinalizeModelRegistration();
    }
}