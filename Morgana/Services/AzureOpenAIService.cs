using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Extensions.AI;
using Morgana.Interfaces;

namespace Morgana.Services;

public class AzureOpenAIService : ILLMService
{
    private readonly IConfiguration _config;
    private readonly IChatClient _chatClient;

    public AzureOpenAIService(IConfiguration config)
    {
        _config = config;

        var endpoint = new Uri(_config["Azure:OpenAI:Endpoint"]!);
        var credential = new AzureCliCredential();
        var deploymentName = _config["Azure:OpenAI:DeploymentName"]!;

        var azureClient = new AzureOpenAIClient(endpoint, credential);
        _chatClient = azureClient.GetChatClient(deploymentName).AsIChatClient();
    }

    public IChatClient GetChatClient() => _chatClient;

    public async Task<string> CompleteAsync(string prompt)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "Sei un assistente utile e professionale."),
            new(ChatRole.User, prompt)
        };

        var response = await _chatClient.GetResponseAsync(messages);
        return response.Text ?? string.Empty;
    }

    public async Task<string> CompleteWithSystemPromptAsync(string systemPrompt, string userPrompt)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var response = await _chatClient.GetResponseAsync(messages);
        return response.Text ?? string.Empty;
    }
}