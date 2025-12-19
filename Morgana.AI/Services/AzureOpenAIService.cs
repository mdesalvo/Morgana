using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

public class AzureOpenAIService : MorganaLLMService
{
    public AzureOpenAIService(
        IConfiguration configuration,
        IPromptResolverService promptResolverService) : base(configuration, promptResolverService)
    {
        AzureOpenAIClient azureClient = new AzureOpenAIClient(
            new Uri(this.configuration["LLM:AzureOpenAI:Endpoint"]!),
            new AzureKeyCredential(this.configuration["LLM:AzureOpenAI:ApiKey"]!));
        string deploymentName = this.configuration["LLM:AzureOpenAI:DeploymentName"]!;

        chatClient = azureClient.GetChatClient(deploymentName).AsIChatClient();
    }
}