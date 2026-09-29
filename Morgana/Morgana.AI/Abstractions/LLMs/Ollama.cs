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
/// Ollama provider for local models. Configuration under Morgana:LLM:Ollama with Endpoint (e.g., http://localhost:11434)
/// and Tiers map. Choose a model with solid function calling support (e.g., gpt-oss:20b, phi4-mini) because Morgana
/// relies heavily on tool calling. Before starting Morgana, verify your model is already loaded via "ollama ps".
/// Typical dev setup: declare only Efficiency tier (all agents use it). If your agent requires Performance tier,
/// you must load a distinct second model (no cross-tier fallback available).
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

        // A local server neither throttles nor sheds load, so nothing here is retried: the timeout
        // bounds the one attempt a call gets. It is generous because a local model on modest
        // hardware can take minutes to answer a long prompt.
        TimeSpan timeout = TimeSpan.FromSeconds(this.configuration.GetValue("Morgana:LLM:Ollama:TimeoutSeconds", 180));

        // Ollama's client binds its model at construction (unlike the SDK-based providers,
        // there is no single client + per-call model selection), so one OllamaApiClient per
        // configured tier — each with its own HttpClient, since HttpClient.BaseAddress is
        // fixed but the model differs and OllamaApiClient does not expose overriding it later.
        foreach ((Records.LLMTier tier, Records.TierDefinition tierDefinition) in tiers)
        {
            HttpClient httpClient = CreateAttemptLoggingHttpClient(timeout);
            httpClient.BaseAddress = endpoint;

            IChatClient tierClient = WrapWithTelemetry(new OllamaApiClient(httpClient, tierDefinition.Options.ModelId));
            RegisterTierClient(tier, tierDefinition.Options.ModelId, tierClient, tierDefinition.MagicDust, tierDefinition.Options.ToChatOptions());
        }

        // Wraps up tier registration and picks which client the framework's own actors
        // (Guard, Classifier, Presenter, ChannelAdapter) will use.
        FinalizeModelRegistration();
    }
}