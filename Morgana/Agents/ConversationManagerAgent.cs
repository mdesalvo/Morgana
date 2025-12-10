using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Event;
using Morgana.Interfaces;
using static Morgana.Records;

namespace Morgana.Agents;

public class ConversationManagerAgent : MorganaAgent
{
    private readonly ISignalRBridgeService signalRBridge;
    private readonly ILoggingAdapter logger = Context.GetLogger();
    private IActorRef? supervisor;

    public ConversationManagerAgent(string conversationId, string userId, ISignalRBridgeService signalRBridge) : base(conversationId, userId)
    {
        this.signalRBridge = signalRBridge;

        ReceiveAsync<UserMessage>(HandleUserMessageAsync);
        ReceiveAsync<CreateConversation>(HandleCreateConversationAsync);
        ReceiveAsync<TerminateConversation>(HandleTerminateConversationAsync);
    }

    private Task HandleCreateConversationAsync(CreateConversation msg)
    {
        IActorRef originalSender = Sender;

        logger.Info($"Creating conversation {msg.ConversationId} for user {msg.UserId}");

        if (supervisor == null)
        {
            DependencyResolver? resolver = DependencyResolver.For(Context.System);
            Props? supProps = resolver.Props<ConversationSupervisorAgent>(msg.ConversationId, msg.UserId);
            supervisor = Context.ActorOf(supProps, $"supervisor-{msg.ConversationId}");
            Context.Watch(supervisor);
            logger.Info("Supervisor created: {0}", supervisor.Path);
        }

        originalSender.Tell(new ConversationCreated(msg.ConversationId, msg.UserId));

        return Task.CompletedTask;
    }

    private Task HandleTerminateConversationAsync(TerminateConversation msg)
    {
        logger.Info($"Terminating conversation {msg.ConversationId} for user {msg.UserId}");

        if (supervisor != null)
        {
            Context.Stop(supervisor);
            logger.Info("Supervisor stopped for conversation {0}", msg.ConversationId);
            supervisor = null;
        }

        return Task.CompletedTask;
    }

    private async Task HandleUserMessageAsync(UserMessage msg)
    {
        logger.Info($"Received message in conversation {conversationId} from user {userId}: {msg.Text}");

        if (supervisor == null)
        {
            // fallback preventivo
            DependencyResolver? resolver = DependencyResolver.For(Context.System);
            Props? supProps = resolver.Props<ConversationSupervisorAgent>(msg.ConversationId, msg.UserId);
            supervisor = Context.ActorOf(supProps, $"supervisor-{msg.ConversationId}");
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
            await signalRBridge.SendMessageToConversationAsync(conversationId, userId, conversationResponse.Response);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to send SignalR message");
        }
    }

    protected override void PreStart()
    {
        logger.Info($"ConversationManager started for {conversationId} for user {userId}");
        base.PreStart();
    }

    protected override void PostStop()
    {
        logger.Info($"ConversationManager stopped for {conversationId} for user {userId}");
        base.PostStop();
    }
}