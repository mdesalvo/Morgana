using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

public class AnthropicService : ILLMService
{
    private readonly IConfiguration configuration;
    private readonly IChatClient chatClient;
    private readonly IPromptResolverService promptResolverService;
    private readonly Prompt morganaPrompt;

    public AnthropicService(
        IConfiguration configuration,
        IPromptResolverService promptResolverService)
    {
        this.configuration = configuration;
        this.promptResolverService = promptResolverService;

        AnthropicClient anthropicClient = new AnthropicClient(
            new ClientOptions
            {
                APIKey = this.configuration["Anthropic:ApiKey"]!
            });
        string anthropicModel = this.configuration["Anthropic:Model"]!;

        chatClient = anthropicClient.AsIChatClient(anthropicModel);
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