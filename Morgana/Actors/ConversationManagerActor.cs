using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Event;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using Morgana.Interfaces;
using static Morgana.Records;

namespace Morgana.Actors;

public class ConversationManagerActor : MorganaActor
{
    private readonly ISignalRBridgeService signalRBridgeService;
    private readonly ILoggingAdapter logger = Context.GetLogger();

    // Supervisor attivo
    private IActorRef? supervisorActor;

    public ConversationManagerActor(
        string conversationId,
        ISignalRBridgeService signalRBridgeService,
        ILLMService llmService,
        IPromptResolverService promptResolverService) : base(conversationId, llmService, promptResolverService)
    {
        this.signalRBridgeService = signalRBridgeService;

        ReceiveAsync<UserMessage>(HandleUserMessageAsync);
        ReceiveAsync<CreateConversation>(HandleCreateConversationAsync);
        ReceiveAsync<TerminateConversation>(HandleTerminateConversationAsync);
    }

    private Task HandleCreateConversationAsync(CreateConversation msg)
    {
        IActorRef senderRef = Sender;

        logger.Info($"Creating conversation {msg.ConversationId}");

        if (supervisorActor == null)
        {
            Props? supProps = DependencyResolver.For(Context.System)
                .Props<ConversationSupervisorActor>(msg.ConversationId);
            supervisorActor = Context.ActorOf(supProps, $"supervisor-{msg.ConversationId}");
            Context.Watch(supervisorActor);
            logger.Info("Supervisor created: {0}", supervisorActor.Path);
        }

        senderRef.Tell(new ConversationCreated(msg.ConversationId));

        return Task.CompletedTask;
    }

    private Task HandleTerminateConversationAsync(TerminateConversation msg)
    {
        logger.Info($"Terminating conversation {msg.ConversationId}");

        if (supervisorActor != null)
        {
            Context.Stop(supervisorActor);
            logger.Info("Supervisor stopped for conversation {0}", msg.ConversationId);
            supervisorActor = null;
        }

        return Task.CompletedTask;
    }

    private async Task HandleUserMessageAsync(UserMessage msg)
    {
        logger.Info($"Received message in conversation {conversationId}: {msg.Text}");

        if (supervisorActor == null)
        {
            // fallback preventivo
            Props? supProps = DependencyResolver.For(Context.System)
                .Props<ConversationSupervisorActor>(msg.ConversationId);
            supervisorActor = Context.ActorOf(supProps, $"supervisor-{msg.ConversationId}");
            Context.Watch(supervisorActor);
            logger.Warning("Supervisor was missing; created new supervisor: {0}", supervisorActor.Path);
        }

        logger.Info("Forwarding message to supervisor at {0}", supervisorActor.Path);

        ConversationResponse conversationResponse;
        try
        {
            conversationResponse = await supervisorActor.Ask<ConversationResponse>(msg);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Supervisor did not reply in time");
            conversationResponse = new ConversationResponse("Si è verificato un errore interno.", "error", []);
        }

        // invia al client via SignalR (bridge)
        try
        {
            await signalRBridgeService.SendMessageToConversationAsync(conversationId, conversationResponse.Response);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to send SignalR message");
        }
    }

    protected override void PreStart()
    {
        logger.Info($"ConversationManagerActor started for {conversationId}");
        base.PreStart();
    }

    protected override void PostStop()
    {
        logger.Info($"ConversationManagerActor stopped for {conversationId}");
        base.PostStop();
    }
}