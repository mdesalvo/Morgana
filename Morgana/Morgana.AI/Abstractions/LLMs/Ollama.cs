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
///         "Model": "your-ollama-model" //e.g: gpt-oss:20b, phi4-mini, ...
///       }
///     }
///   }
/// }
/// </code>
/// <para><strong>Important Notes:</strong></para>
/// <para>- Morgana is an AI orchestrator which relies heavily on tool calling (context variables, quick replies, rich cards).
/// For best result, please choose a model with solid function calling support (e.g: gpt-oss:20b, phi4-mini).</para>
/// <para>- Before starting Morgana, check with "ollama ps" that your model is already loaded into memory!</para>
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
        // Get chat client for specific Ollama model (it is already compatible with Microsoft.Extensions.AI abstraction)
        // and wrap with the MEAI OpenTelemetry decorator for gen_ai.* spans and metrics.
        chatClient = WrapWithTelemetry(
            new OllamaApiClient(
                new HttpClient
                {
                    BaseAddress = new Uri(this.configuration["Morgana:LLM:Ollama:Endpoint"]!),
                    Timeout = TimeSpan.FromSeconds(Convert.ToInt32(this.configuration["Morgana:ActorSystem:TimeoutSeconds"]))
                }, this.configuration["Morgana:LLM:Ollama:Model"]!));
    }
}