using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Morgana.Interfaces;

namespace Morgana.Services;

public class AzureOpenAIService : ILLMService
{
    private readonly IConfiguration configuration;
    private readonly IChatClient chatClient;

    public AzureOpenAIService(IConfiguration configuration)
    {
        this.configuration = configuration;

        Uri endpoint = new Uri(this.configuration["Azure:OpenAI:Endpoint"]!);
        AzureCliCredential credential = new AzureCliCredential();
        AzureOpenAIClient azureClient = new AzureOpenAIClient(endpoint, credential);
        string deploymentName = this.configuration["Azure:OpenAI:DeploymentName"]!;

        chatClient = azureClient.GetChatClient(deploymentName).AsIChatClient();
    }

    public IChatClient GetChatClient() => chatClient;

    public async Task<string> CompleteAsync(string prompt)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, "Sei un assistente utile e professionale."),
            new(ChatRole.User, prompt)
        ];

        ChatResponse response = await chatClient.GetResponseAsync(messages);
        return response.Text;
    }

    public async Task<string> CompleteWithSystemPromptAsync(string systemPrompt, string userPrompt)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        ];

        ChatResponse response = await chatClient.GetResponseAsync(messages);
        return response.Text;
    }
}