using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

public class AzureOpenAIService : ILLMService
{
    private readonly IConfiguration configuration;
    private readonly IChatClient chatClient;
    private readonly IPromptResolverService promptResolverService;
    private readonly Prompt morganaPrompt;

    public AzureOpenAIService(
        IConfiguration configuration,
        IPromptResolverService promptResolverService)
    {
        this.configuration = configuration;
        this.promptResolverService = promptResolverService;

        AzureOpenAIClient azureClient = new AzureOpenAIClient(
            new Uri(this.configuration["Azure:OpenAI:Endpoint"]!),
            new AzureKeyCredential(this.configuration["Azure:OpenAI:ApiKey"]!));
        string deploymentName = this.configuration["Azure:OpenAI:DeploymentName"]!;

        chatClient = azureClient.GetChatClient(deploymentName).AsIChatClient();
        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    public IChatClient GetChatClient() => chatClient;
    public IPromptResolverService GetPromptResolverService() => promptResolverService;

    public async Task<string> CompleteAsync(string conversationId, string prompt)
        => await CompleteWithSystemPromptAsync(conversationId, morganaPrompt.Content, prompt);

    public async Task<string> CompleteWithSystemPromptAsync(string conversationId, string systemPrompt, string userPrompt)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt)
        ];

        ChatOptions chatOptions = new ChatOptions
        {
            ConversationId = conversationId,
        };

        ChatResponse response = await chatClient.GetResponseAsync(messages, chatOptions);
        return response.Text;
    }
}