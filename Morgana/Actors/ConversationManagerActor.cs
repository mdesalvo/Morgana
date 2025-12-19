using Akka.Actor;
using Akka.Event;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.Interfaces;
using static Morgana.Records;

namespace Morgana.Actors;

public class ConversationManagerActor : MorganaActor
{
    private readonly ISignalRBridgeService signalRBridgeService;
    private readonly ILoggingAdapter logger = Context.GetLogger();

    // Supervisor attivo
    private IActorRef? supervisor;

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

    private async Task HandleCreateConversationAsync(CreateConversation msg)
    {
        IActorRef senderRef = Sender;

        logger.Info($"Creating conversation {msg.ConversationId}");

        if (supervisor is null)
        {
            supervisor = await Context.System.GetOrCreateActor<ConversationSupervisorActor>(
                "supervisor", msg.ConversationId);

            Context.Watch(supervisor);
            logger.Info("Supervisor created: {0}", supervisor.Path);
        }

        senderRef.Tell(new ConversationCreated(msg.ConversationId));
    }

    private Task HandleTerminateConversationAsync(TerminateConversation msg)
    {
        logger.Info($"Terminating conversation {msg.ConversationId}");

        if (supervisor is not null)
        {
            Context.Stop(supervisor);
            supervisor = null;

            logger.Info("Supervisor stopped for conversation {0}", msg.ConversationId);
        }

        return Task.CompletedTask;
    }

    private async Task HandleUserMessageAsync(UserMessage msg)
    {
        logger.Info($"Received message in conversation {conversationId}: {msg.Text}");

        if (supervisor == null)
        {
            supervisor = await Context.System.GetOrCreateActor<ConversationSupervisorActor>(
                "supervisor", msg.ConversationId);

            Context.Watch(supervisor);
            logger.Warning("Supervisor was missing; created new supervisor: {0}", supervisor.Path);
        }

        logger.Info("Forwarding message to supervisor at {0}", supervisor.Path);

        ConversationResponse conversationResponse;
        try
        {
            conversationResponse = await supervisor.Ask<ConversationResponse>(msg);
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