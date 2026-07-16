using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using OpenAI;

namespace Morgana.AI.Abstractions.LLMs;

/// <summary>
/// OpenAI implementation of ILLMService.<br/>
/// Supports GPT models via OpenAI Service (gpt-4o, gpt-4o-mini, ...)
/// </summary>
/// <remarks>
/// <para><strong>Configuration (appsettings.json):</strong></para>
/// <code>
/// {
///   "Morgana: {
///     "LLM": {
///       "Provider": "openai",
///       "OpenAI": {
///         "ApiKey": "your-api-key",
///         "Models": {
///           "Low": { "Name": "gpt-4o-mini", "MagicDust": { ... } },
///           "Moderate": { "Name": "gpt-4o", "MagicDust": { ... } }
///         }
///       }
///     }
///   }
/// }
/// </code>
/// </remarks>
public class OpenAI : MorganaLLM
{
    /// <summary>
    /// Initializes a new instance of OpenAI.
    /// Creates OpenAI client and wraps it with Microsoft.Extensions.AI IChatClient.
    /// </summary>
    /// <param name="configuration">Application configuration containing OpenAI key and model</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="loggerFactory">Optional logger factory used to instrument the chat client with the MEAI OpenTelemetry decorator.</param>
    public OpenAI(
        IConfiguration configuration,
        IPromptResolverService promptResolverService,
        ILoggerFactory? loggerFactory = null) : base(configuration, promptResolverService, loggerFactory)
    {
        OpenAIClient openaiClient = new OpenAIClient(
            new ApiKeyCredential(this.configuration["Morgana:LLM:OpenAI:ApiKey"]!));

        // Binds the models declared in configuration so they're available at runtime for
        // matching against each agent's declared tier (see Records.ModelDefinition remarks
        // for how the config layout is structured).
        Dictionary<Records.LLMTier, Records.ModelDefinition> models =
            this.configuration.GetSection("Morgana:LLM:OpenAI:Models").Get<Dictionary<Records.LLMTier, Records.ModelDefinition>>() ?? [];

        // One chat client per configured tier, sharing the same OpenAIClient (API key only),
        // wrapped with the MEAI OpenTelemetry decorator for gen_ai.* spans and metrics.
        foreach ((Records.LLMTier tier, Records.ModelDefinition model) in models)
            RegisterTierClient(tier, model.Name, WrapWithTelemetry(openaiClient.GetChatClient(model.Name).AsIChatClient()), model.MagicDust);

        // Wraps up tier registration and picks which client the framework's own actors
        // (Guard, Classifier, Presenter, ChannelAdapter) will use.

        FinalizeModelRegistration();
    }
}