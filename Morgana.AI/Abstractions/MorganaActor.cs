using Akka.Actor;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions;

public class MorganaActor : ReceiveActor
{
    protected readonly string conversationId;
    protected readonly ILLMService llmService;
    protected readonly IPromptResolverService promptResolverService;

    protected MorganaActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService)
    {
        this.conversationId = conversationId;
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
    }
}