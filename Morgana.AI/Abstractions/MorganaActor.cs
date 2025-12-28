using Akka.Actor;
using Akka.Event;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions;

public class MorganaActor : ReceiveActor
{
    protected readonly string conversationId;
    protected readonly ILLMService llmService;
    protected readonly IPromptResolverService promptResolverService;
    protected readonly ILoggingAdapter actorLogger;

    protected MorganaActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService)
    {
        this.conversationId = conversationId;
        this.llmService = llmService;
        this.promptResolverService = promptResolverService;
        actorLogger = Context.GetLogger();

        // Timeout globale per tutti i MorganaActor
        SetReceiveTimeout(TimeSpan.FromSeconds(60));
        Receive<ReceiveTimeout>(HandleReceiveTimeout);
    }

    protected virtual void HandleReceiveTimeout(ReceiveTimeout timeout)
    {
        actorLogger.Warning($"{GetType().Name} receive timeout");
    }
}