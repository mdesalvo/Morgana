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
///         "DeploymentName": "your-deployment-name"
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
///         "DeploymentName": "your-model-deployment-name"
///       }
///     }
///   }
/// }
/// </code>
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
        string deploymentName = this.configuration["Morgana:LLM:AzureOpenAI:DeploymentName"]!;

        // Azure AI Foundry projects expose an OpenAI-compatible unified "v1" API surface
        // (path containing "/openai/v1") that rejects the "api-version" query parameter that
        // AzureOpenAIClient always appends. For these endpoints, the vanilla OpenAI client
        // (pointed at the Foundry endpoint) must be used instead.
        IChatClient innerChatClient;
        if (endpoint.AbsolutePath.Contains("/openai/v1", StringComparison.OrdinalIgnoreCase))
        {
            // Azure AI Foundry
            OpenAIClientOptions clientOptions = new OpenAIClientOptions { Endpoint = endpoint };
            OpenAIClient foundryClient = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions);
            innerChatClient = foundryClient.GetChatClient(deploymentName).AsIChatClient();
        }
        else
        {
            // Legacy AzureOpenAI
            AzureOpenAIClient azureClient = new AzureOpenAIClient(endpoint, new AzureKeyCredential(apiKey));
            innerChatClient = azureClient.GetChatClient(deploymentName).AsIChatClient();
        }

        // Wrap with the MEAI OpenTelemetry decorator for gen_ai.* spans and metrics (input/output tokens, latency, errors).
        chatClient = WrapWithTelemetry(innerChatClient);
    }
}