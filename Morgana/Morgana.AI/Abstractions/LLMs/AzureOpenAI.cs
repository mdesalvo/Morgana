using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions.LLMs;

/// <summary>
/// Azure OpenAI implementation of ILLMService.<br/>
/// Supports GPT models deployed via Azure OpenAI Service (gpt-4o, ...)
/// </summary>
/// <remarks>
/// <para><strong>Configuration (appsettings.json):</strong></para>
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
/// </remarks>
public class AzureOpenAI : MorganaLLM
{
    /// <summary>
    /// Initializes a new instance of AzureOpenAI.
    /// Creates Azure OpenAI client and wraps it with Microsoft.Extensions.AI IChatClient.
    /// </summary>
    /// <param name="configuration">Application configuration containing Azure endpoint, key, and deployment</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    public AzureOpenAI(
        IConfiguration configuration,
        IPromptResolverService promptResolverService) : base(configuration, promptResolverService)
    {
        AzureOpenAIClient azureClient = new AzureOpenAIClient(
            new Uri(this.configuration["Morgana:LLM:AzureOpenAI:Endpoint"]!),
            new AzureKeyCredential(this.configuration["Morgana:LLM:AzureOpenAI:ApiKey"]!));
        string deploymentName = this.configuration["Morgana:LLM:AzureOpenAI:DeploymentName"]!;

        // Get chat client for specific deployment and wrap with Microsoft.Extensions.AI abstraction
        chatClient = azureClient.GetChatClient(deploymentName).AsIChatClient();
    }
}
