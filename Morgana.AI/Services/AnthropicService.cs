using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

public class AnthropicService : MorganaLLMService
{
    public AnthropicService(
        IConfiguration configuration,
        IPromptResolverService promptResolverService) : base(configuration, promptResolverService)
    {
        AnthropicClient anthropicClient = new AnthropicClient(
            new ClientOptions
            {
                APIKey = this.configuration["LLM:Anthropic:ApiKey"]!
            });
        string anthropicModel = this.configuration["LLM:Anthropic:Model"]!;

        chatClient = anthropicClient.AsIChatClient(anthropicModel);
    }
}