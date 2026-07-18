using System.ClientModel;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using OpenAI;

namespace Morgana.AI.Abstractions.LLMs;

/// <summary>
/// Azure OpenAI implementation of ILLMService.<br/>
/// Supports GPT models deployed via classic Azure OpenAI Service (gpt-4o, ...) as well as
/// Azure AI Foundry projects exposing the unified OpenAI-compatible v1 API (gpt-5.x, ...)
/// </summary>
/// <remarks>
/// <para><strong>Configuration (appsettings.json) - classic Azure OpenAI:</strong></para>
/// <code>
/// {
///   "Morgana: {
///     "LLM": {
///       "Provider": "azureopenai",
///       "AzureOpenAI": {
///         "Endpoint": "https://your-resource.openai.azure.com/",
///         "ApiKey": "your-api-key",
///         "Tiers": {
///           "Efficiency": { "Options": { "ModelId": "gpt-4o-mini", "MaxOutputTokens": 8192 }, "MagicDust": { ... } }
///         }
///       }
///     }
///   }
/// }
/// </code>
/// <para><strong>Configuration (appsettings.json) - Azure AI Foundry v1 API:</strong></para>
/// <code>
/// {
///   "Morgana: {
///     "LLM": {
///       "Provider": "azureopenai",
///       "AzureOpenAI": {
///         "Endpoint": "https://your-resource.services.ai.azure.com/api/projects/your-project/openai/v1",
///         "ApiKey": "your-api-key",
///         "Tiers": {
///           "Efficiency": { "Options": { "ModelId": "your-efficiency-tier-deployment-name", "MaxOutputTokens": 4096 }, "MagicDust": { ... } }
///         }
///       }
///     }
///   }
/// }
/// </code>
/// <para><strong>MagicDust:</strong> uses the same values as the <see cref="OpenAI"/> provider
/// (gpt-4o-mini/gpt-4o reference pricing on <c>Efficiency</c>, floored on <c>Performance</c> —
/// see <see cref="Records.MagicDustPricing"/> remarks for the formula). A Foundry deployment
/// name carries no pricing information Morgana can introspect, and Azure deployments run from
/// "mini" variants to flagship models with no fixed price relationship between them —
/// recalibrate whenever a tier's <c>ModelId</c> changes.</para>
/// </remarks>
public class AzureOpenAI : MorganaLLM
{
    /// <summary>
    /// Initializes a new instance of AzureOpenAI.
    /// Creates an Azure OpenAI (or Azure AI Foundry) client and wraps it with Microsoft.Extensions.AI IChatClient.
    /// </summary>
    /// <param name="configuration">Application configuration containing Azure endpoint, key, and deployment</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="loggerFactory">Optional logger factory used to instrument the chat client with the MEAI OpenTelemetry decorator.</param>
    public AzureOpenAI(
        IConfiguration configuration,
        IPromptResolverService promptResolverService,
        ILoggerFactory? loggerFactory = null) : base(configuration, promptResolverService, loggerFactory)
    {
        Uri endpoint = new Uri(this.configuration["Morgana:LLM:AzureOpenAI:Endpoint"]!);
        string apiKey = this.configuration["Morgana:LLM:AzureOpenAI:ApiKey"]!;

        // Binds the tiers declared in configuration so they're available at runtime for
        // matching against each agent's declared tier (see Records.TierDefinition remarks
        // for how the config layout is structured).
        Dictionary<Records.LLMTier, Records.TierDefinition> tiers =
            this.configuration.GetSection("Morgana:LLM:AzureOpenAI:Tiers").Get<Dictionary<Records.LLMTier, Records.TierDefinition>>() ?? [];

        // Azure AI Foundry projects expose an OpenAI-compatible unified "v1" API surface
        // (path containing "/openai/v1") that rejects the "api-version" query parameter that
        // AzureOpenAIClient always appends. For these endpoints, the vanilla OpenAI client
        // (pointed at the Foundry endpoint) must be used instead. Either underlying client is
        // built once and reused across every configured tier — each tier only differs by
        // deployment name (TierDefinition.Options.ModelId).
        bool isFoundryV1 = endpoint.AbsolutePath.Contains("/openai/v1", StringComparison.OrdinalIgnoreCase);
        OpenAIClient? foundryClient = isFoundryV1
            ? new OpenAIClient(new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = endpoint })
            : null;
        AzureOpenAIClient? azureClient = isFoundryV1
            ? null
            : new AzureOpenAIClient(endpoint, new AzureKeyCredential(apiKey));

        foreach ((Records.LLMTier tier, Records.TierDefinition tierDefinition) in tiers)
        {
            // Picks whichever of the two client flavors was actually built above, matching the
            // endpoint style detected for this deployment.
            IChatClient innerChatClient = isFoundryV1
                ? foundryClient!.GetChatClient(tierDefinition.Options.ModelId).AsIChatClient()
                : azureClient!.GetChatClient(tierDefinition.Options.ModelId).AsIChatClient();

            // Wrap with the MEAI OpenTelemetry decorator for gen_ai.* spans and metrics (input/output tokens, latency, errors).
            RegisterTierClient(tier, tierDefinition.Options.ModelId, WrapWithTelemetry(innerChatClient), tierDefinition.MagicDust, tierDefinition.Options.ToChatOptions());
        }

        // Wraps up tier registration and picks which client the framework's own actors
        // (Guard, Classifier, Presenter, ChannelAdapter) will use.
        FinalizeModelRegistration();
    }
}