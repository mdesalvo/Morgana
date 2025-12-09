using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Event;
using Morgana.Interfaces;
using Morgana.Messages;

namespace Morgana.Agents;

public class ConversationManagerAgent : MorganaAgent, IWithTimers
{
    private readonly ISignalRBridgeService signalRBridge;
    private readonly ILoggingAdapter logger = Context.GetLogger();

    public ITimerScheduler Timers { get; set; } = null!;

    public ConversationManagerAgent(string conversationId, string userId, ISignalRBridgeService signalRBridge) : base(conversationId, userId)
    {
        this.signalRBridge = signalRBridge;

        ReceiveAsync<UserMessage>(HandleUserMessageAsync);
        ReceiveAsync<ConversationTimeout>(HandleTimeoutAsync);
        ReceiveAsync<CreateConversation>(HandleCreateConversationAsync);
        ReceiveAsync<TerminateConversation>(HandleTerminateConversationAsync);

        // Timer per timeout conversazione
        Timers.StartSingleTimer("timeout", new ConversationTimeout(), TimeSpan.FromMinutes(15));
    }

    private Task HandleCreateConversationAsync(CreateConversation msg)
    {
        IActorRef originalSender = Sender;

        logger.Info($"Creating conversation {msg.ConversationId} for user {msg.UserId}");

        Props? props = DependencyResolver.For(Context.System)
                                         .Props<ConversationManagerAgent>(msg.ConversationId, msg.UserId);

        IActorRef? manager = Context.ActorOf(props, $"manager-{msg.ConversationId}");

        Context.Watch(manager);

        logger.Info($"Created conversation: {manager.Path}");

        originalSender.Tell(new ConversationCreated(msg.ConversationId, msg.UserId));

        return Task.CompletedTask;
    }

    private Task HandleTerminateConversationAsync(TerminateConversation msg)
    {
        logger.Info($"Terminating conversation {msg.ConversationId} for user {msg.UserId}");

        Props? props = DependencyResolver.For(Context.System)
                                         .Props<ConversationManagerAgent>(msg.ConversationId, msg.UserId);

        IActorRef? manager = Context.ActorOf(props, $"manager-{msg.ConversationId}");

        Context.Stop(manager);

        logger.Info($"Terminated conversation manager: {manager.Path}");

        return Task.CompletedTask;
    }

    private async Task HandleUserMessageAsync(UserMessage msg)
    {
        logger.Info($"Received message in conversation {conversationId} from user {userId}: {msg.Text}");

        Timers.StartSingleTimer("timeout", new ConversationTimeout(), TimeSpan.FromMinutes(15));

        Props? props = DependencyResolver.For(Context.System)
                                         .Props<ConversationSupervisorAgent>(msg.ConversationId, msg.UserId);

        IActorRef? supervisorAgent = Context.ActorOf(props, $"supervisor-{msg.ConversationId}");

        ConversationResponse conversationResponse = await supervisorAgent.Ask<ConversationResponse>(msg);

        await signalRBridge.SendMessageToConversationAsync(conversationId, userId, conversationResponse.Response);
    }

    private Task HandleTimeoutAsync(ConversationTimeout msg)
    {
        logger.Info($"Conversation {conversationId} for user {userId} timed out");
        Context.Parent.Tell(new TerminateConversation(conversationId, userId));
        Context.Stop(Self);

        return Task.CompletedTask;
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