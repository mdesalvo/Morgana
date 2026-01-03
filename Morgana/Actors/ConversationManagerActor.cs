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
        
        // Handle supervisor responses (PipeTo pattern)
        ReceiveAsync<SupervisorResponseContext>(HandleSupervisorResponseAsync);
        ReceiveAsync<Status.Failure>(HandleSupervisorFailureAsync);
    }

    private async Task HandleCreateConversationAsync(CreateConversation msg)
    {
        IActorRef senderRef = Sender;

        actorLogger.Info($"Creating conversation {msg.ConversationId}");

        if (supervisor is null)
        {
            supervisor = await Context.System.GetOrCreateActor<ConversationSupervisorActor>("supervisor", msg.ConversationId);

            Context.Watch(supervisor);
            actorLogger.Info("Supervisor created: {0}", supervisor.Path);

            // Trigger automatic presentation
            supervisor.Tell(new GeneratePresentationMessage());
            actorLogger.Info("Presentation generation triggered");
        }

        senderRef.Tell(new ConversationCreated(msg.ConversationId));
    }

    private Task HandleTerminateConversationAsync(TerminateConversation msg)
    {
        actorLogger.Info($"Terminating conversation {msg.ConversationId}");

        if (supervisor is not null)
        {
            Context.Stop(supervisor);
            supervisor = null;

            actorLogger.Info("Supervisor stopped for conversation {0}", msg.ConversationId);
        }

        return Task.CompletedTask;
    }

    private async Task HandleUserMessageAsync(UserMessage msg)
    {
        actorLogger.Info($"Received message in conversation {conversationId}: {msg.Text}");

        if (supervisor == null)
        {
            supervisor = await Context.System.GetOrCreateActor<ConversationSupervisorActor>("supervisor", msg.ConversationId);
            Context.Watch(supervisor);

            actorLogger.Warning("Supervisor was missing; created new supervisor: {0}", supervisor.Path);
        }

        actorLogger.Info("Forwarding message to supervisor at {0}", supervisor.Path);

        // PipeTo pattern: non-blocking, with timeout
        supervisor.Ask<ConversationResponse>(msg, TimeSpan.FromSeconds(60))
            .PipeTo(Self,
                success: response => new SupervisorResponseContext(response),
                failure: ex => new Status.Failure(ex));
    }

    private async Task HandleSupervisorResponseAsync(SupervisorResponseContext ctx)
    {
        actorLogger.Info(
            $"Received response from supervisor (agent: {ctx.Response.AgentName ?? "unknown"}): " +
            $"{ctx.Response.Response[..Math.Min(50, ctx.Response.Response.Length)]}...");

        // Send response to client via SignalR
        try
        {
            await signalRBridgeService.SendStructuredMessageAsync(
                conversationId, 
                ctx.Response.Response,
                "assistant",
                null,
                null,
                ctx.Response.AgentName);
            
            actorLogger.Info("Response sent successfully to client via SignalR");
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send SignalR message to client");
            
            // Attempt to send error notification to client
            try
            {
                await signalRBridgeService.SendStructuredMessageAsync(
                    conversationId,
                    "Si è verificato un errore nell'invio della risposta.",
                    "assistant",
                    null,
                    "delivery_error",
                    "Morgana");
            }
            catch (Exception fallbackEx)
            {
                actorLogger.Error(fallbackEx, "Failed to send error notification to client");
            }
        }
    }

    private async Task HandleSupervisorFailureAsync(Status.Failure failure)
    {
        actorLogger.Error(failure.Cause, "Supervisor did not reply in time or failed");

        // Send error message to client via SignalR
        try
        {
            await signalRBridgeService.SendStructuredMessageAsync(
                conversationId,
                "Si è verificato un errore interno.",
                "assistant",
                null,
                "supervisor_error",
                "Morgana");
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send error message to client");
        }
    }

    protected override void PreStart()
    {
        actorLogger.Info($"ConversationManagerActor started for {conversationId}");
        base.PreStart();
    }

    protected override void PostStop()
    {
        actorLogger.Info($"ConversationManagerActor stopped for {conversationId}");
        base.PostStop();
    }
}