using Microsoft.Extensions.AI;

namespace Morgana.AI.Interfaces;

public interface ILLMService
{
    Task<string> CompleteAsync(string prompt);
    Task<string> CompleteWithSystemPromptAsync(string systemPrompt, string userPrompt);
    IChatClient GetChatClient();
}