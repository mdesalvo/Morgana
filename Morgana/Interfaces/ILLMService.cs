using Microsoft.Extensions.AI;

namespace Morgana.Interfaces;

public interface ILLMService
{
    Task<string> CompleteAsync(string prompt);
    Task<string> CompleteWithSystemPromptAsync(string systemPrompt, string userPrompt);
    IChatClient GetChatClient();
}