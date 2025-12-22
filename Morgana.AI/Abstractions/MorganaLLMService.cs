using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.AI.Abstractions
{
    public class MorganaLLMService : ILLMService
    {
        protected readonly IConfiguration configuration;
        protected readonly IPromptResolverService promptResolverService;
        protected readonly Prompt morganaPrompt;
        protected IChatClient chatClient;

        public MorganaLLMService(
            IConfiguration configuration,
            IPromptResolverService promptResolverService)
        {
            this.configuration = configuration;
            this.promptResolverService = promptResolverService;

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
                new ChatMessage(ChatRole.System, systemPrompt),
                new ChatMessage(ChatRole.User, userPrompt)
            ];

            ChatOptions chatOptions = new ChatOptions
            {
                ConversationId = conversationId
            };

            try
            {
                ChatResponse response = await chatClient.GetResponseAsync(messages, chatOptions);
                return response.Text
                    .Replace("```json", string.Empty)
                    .Replace("```", string.Empty);
            }
            catch (Exception ex)
            {
                List<ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<ErrorAnswer>>("ErrorAnswers");
                ErrorAnswer? llmError = errorAnswers.FirstOrDefault(e => string.Equals(e.Name, "LLMServiceError", StringComparison.OrdinalIgnoreCase));
                
                return llmError?.Content.Replace("((llm_error))", ex.Message) 
                    ?? $"Errore del servizio LLM: {ex.Message}";
            }
        }
    }
}