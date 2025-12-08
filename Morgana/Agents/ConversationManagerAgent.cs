using System.ComponentModel;
using Akka.Actor;
using Akka.DependencyInjection;
using Akka.Event;
using Morgana.Interfaces;
using Morgana.Messages;

namespace Morgana.Agents;

public class ConversationManagerAgent : ReceiveActor, IWithTimers
{
    private readonly string conversationId;
    private readonly string userId;
    private readonly ISignalRBridgeService signalRBridge;
    private readonly ILoggingAdapter logger = Context.GetLogger();

    public ITimerScheduler Timers { get; set; } = null!;

    public ConversationManagerAgent(
        string conversationId, 
        string userId,
        ISignalRBridgeService signalRBridge)
    {
        this.conversationId = conversationId;
        this.userId = userId;
        this.signalRBridge = signalRBridge;
        
        ReceiveAsync<UserMessage>(HandleUserMessage);
        ReceiveAsync<ConversationTimeout>(HandleTimeout);
        ReceiveAsync<CreateConversation>(HandleCreateConversation);
        ReceiveAsync<TerminateConversation>(HandleTerminateConversation);

        // Timer per timeout conversazione
        Timers.StartSingleTimer("timeout", new ConversationTimeout(), TimeSpan.FromMinutes(15));
    }

    private Task HandleCreateConversation(CreateConversation msg)
    {
        logger.Info($"Creating conversation {msg.ConversationId} for user {msg.UserId}");
        
        Props? props = DependencyResolver.For(Context.System)
                                         .Props<ConversationManagerAgent>(msg.ConversationId, msg.UserId);

        IActorRef? manager = Context.ActorOf(props/*, $"conversation-{msg.ConversationId}"*/);

        Context.Watch(manager);

        logger.Info($"Created conversation: {manager.Path}");
        
        Sender.Tell(new ConversationCreated(msg.ConversationId, msg.UserId));

        return Task.CompletedTask;
    }

    private Task HandleTerminateConversation(TerminateConversation msg)
    {
        logger.Info($"Terminating conversation {msg.ConversationId} for user {msg.UserId}");
        
        Props? props = DependencyResolver.For(Context.System)
                                         .Props<ConversationManagerAgent>(msg.ConversationId, msg.UserId);
        
        IActorRef? manager = Context.ActorOf(props/*, $"conversation-{msg.ConversationId}"*/);

        Context.Stop(manager);

        logger.Info($"Terminated conversation manager: {manager.Path}");

        return Task.CompletedTask;
    }

    private Task HandleUserMessage(UserMessage msg)
    {
        logger.Info($"Received message in conversation {conversationId} from user {userId}: {msg.Text}");

        Timers.StartSingleTimer("timeout", new ConversationTimeout(), TimeSpan.FromMinutes(15));

        // TODO: Qui dovrebbe partire il flusso con Supervisor, Guardiano, Classificatore, ecc.
        // Per ora risposta di test immediata
        string response = ProcessMessage(msg);
        
        signalRBridge.SendMessageToConversationAsync(conversationId, userId, response);

        return Task.CompletedTask;
    }

    [Description("Temporary mock for core of HandleUserMessage!!")]
    private string ProcessMessage(UserMessage msg)
    {
        // GUARDIA SEMPLIFICATA per test
        string lowerText = msg.Text.ToLower();
        
        if (lowerText.Contains("stupido") || lowerText.Contains("idiota") || lowerText.Contains("cretino"))
        {
            return "Ti prego di mantenere un tono cortese. Sono qui per aiutarti! 😊";
        }
        
        // Risposta mock per test
        string[] responses = new[]
        {
            $"Ho ricevuto il tuo messaggio: {msg.ConversationId}-{msg.UserId}:{msg.Text}. Come posso aiutarti?",
            "Interessante! Dimmi di più.",
            "Capisco. Vuoi che approfondisca questo argomento?",
            "Sono qui per supportarti. Di cosa hai bisogno?",
            "Ottima domanda! Ecco cosa posso dirti...",
        };
        
        return responses[new Random().Next(responses.Length)];
    }

    private Task HandleTimeout(ConversationTimeout msg)
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