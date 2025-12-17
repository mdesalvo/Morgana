using Microsoft.Extensions.AI;

namespace Morgana.AI.Interfaces;

public interface ILLMService
{
    Task<string> CompleteAsync(string conversationId, string prompt);
    Task<string> CompleteWithSystemPromptAsync(string conversationId, string systemPrompt, string userPrompt);

    IChatClient GetChatClient();
    IPromptResolverService GetPromptResolverService();
}