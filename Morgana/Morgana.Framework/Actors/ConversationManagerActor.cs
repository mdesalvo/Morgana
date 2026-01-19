using Akka.Actor;
using Akka.Event;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Extensions;
using Morgana.Framework.Interfaces;

namespace Morgana.Framework.Actors;

/// <summary>
/// Entry point actor for managing conversations.
/// Responsible for conversation lifecycle (creation/termination) and message routing to the supervisor.
/// Uses PipeTo pattern for non-blocking communication with the supervisor.
/// </summary>
/// <remarks>
/// This actor serves as the primary interface between the external system (SignalR) and the internal actor hierarchy.
/// It maintains a reference to the ConversationSupervisorActor and forwards user messages for processing.
/// </remarks>
public class ConversationManagerActor : MorganaActor
{
    private readonly ISignalRBridgeService signalRBridgeService;

    /// <summary>
    /// Reference to the active conversation supervisor actor.
    /// Null until a conversation is created.
    /// </summary>
    private IActorRef? supervisor;

    /// <summary>
    /// Initializes a new instance of the ConversationManagerActor.
    /// </summary>
    /// <param name="conversationId">Unique identifier for this conversation</param>
    /// <param name="signalRBridgeService">Service for sending messages to clients via SignalR</param>
    /// <param name="llmService">LLM service for AI completions</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    public ConversationManagerActor(
        string conversationId,
        ISignalRBridgeService signalRBridgeService,
        ILLMService llmService,
        IPromptResolverService promptResolverService) : base(conversationId, llmService, promptResolverService)
    {
        this.signalRBridgeService = signalRBridgeService;

        // Handle incoming user messages from SignalR:
        // - Ensures supervisor exists (creates if missing)
        // - Forwards message to supervisor using Ask pattern with PipeTo for non-blocking processing
        // - Includes 60-second timeout for supervisor response
        ReceiveAsync<Records.UserMessage>(HandleUserMessageAsync);

        // Handle conversation lifecycle requests:
        // - CreateConversation: creates supervisor actor, triggers automatic presentation generation
        // - TerminateConversation: stops supervisor actor and clears reference
        ReceiveAsync<Records.CreateConversation>(HandleCreateConversationAsync);
        ReceiveAsync<Records.TerminateConversation>(HandleTerminateConversationAsync);

        // Handle supervisor responses via PipeTo pattern:
        // - SupervisorResponseContext: successful response from supervisor → send to client via SignalR
        // - Status.Failure: timeout or exception from supervisor → send error message to client
        ReceiveAsync<Records.SupervisorResponseContext>(HandleSupervisorResponseAsync);
        ReceiveAsync<Status.Failure>(HandleSupervisorFailureAsync);
    }

    /// <summary>
    /// Handles conversation creation requests.
    /// Creates and watches the supervisor actor, then triggers automatic presentation generation.
    /// </summary>
    /// <param name="msg">Conversation creation request message</param>
    private async Task HandleCreateConversationAsync(Records.CreateConversation msg)
    {
        IActorRef senderRef = Sender;

        actorLogger.Info($"Creating conversation {msg.ConversationId}");

        if (supervisor is null)
        {
            supervisor = await Context.System.GetOrCreateActor<ConversationSupervisorActor>(
                "supervisor", msg.ConversationId);

            Context.Watch(supervisor);

            actorLogger.Info("Supervisor created: {0}", supervisor.Path);

            // Trigger automatic presentation (only in case of new conversation)
            if (!msg.IsRestore)
            {
                supervisor.Tell(new Records.GeneratePresentationMessage());

                actorLogger.Info("Presentation generation triggered");
            }
        }

        senderRef.Tell(new Records.ConversationCreated(msg.ConversationId));
    }

    /// <summary>
    /// Handles conversation termination requests.
    /// Stops the supervisor actor and clears the reference.
    /// </summary>
    /// <param name="msg">Conversation termination request message</param>
    private Task HandleTerminateConversationAsync(Records.TerminateConversation msg)
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

    /// <summary>
    /// Handles incoming user messages.
    /// Ensures supervisor exists, then forwards the message using Ask pattern with PipeTo for non-blocking processing.
    /// </summary>
    /// <param name="msg">User message to process</param>
    /// <remarks>
    /// Uses PipeTo pattern to avoid blocking the actor mailbox while waiting for supervisor response.
    /// Includes 60-second timeout for supervisor processing.
    /// </remarks>
    private async Task HandleUserMessageAsync(Records.UserMessage msg)
    {
        actorLogger.Info($"Received message in conversation {conversationId}: {msg.Text}");

        if (supervisor == null)
        {
            supervisor = await Context.System.GetOrCreateActor<ConversationSupervisorActor>(
                "supervisor", msg.ConversationId);

            Context.Watch(supervisor);

            actorLogger.Warning("Supervisor was missing; created new supervisor: {0}", supervisor.Path);
        }

        actorLogger.Info("Forwarding message to supervisor at {0}", supervisor.Path);

        _ = supervisor
                .Ask<Records.ConversationResponse>(msg, TimeSpan.FromSeconds(60))
                .PipeTo(
                    Self,
                    success: response => new Records.SupervisorResponseContext(response),
                    failure: ex => new Status.Failure(ex));
    }

    /// <summary>
    /// Handles successful responses from the supervisor (via PipeTo).
    /// Sends the response to the client via SignalR with appropriate metadata.
    /// </summary>
    /// <param name="ctx">Context containing the supervisor's response</param>
    private async Task HandleSupervisorResponseAsync(Records.SupervisorResponseContext ctx)
    {
        actorLogger.Info(
            $"Received response from supervisor (agent: {ctx.Response.AgentName ?? "unknown"}, completed: {ctx.Response.AgentCompleted}): " +
            $"{ctx.Response.Response[..Math.Min(50, ctx.Response.Response.Length)]}..., #quickReplies: {ctx.Response.QuickReplies?.Count ?? 0}");

        // Send response to client via SignalR
        try
        {
            await signalRBridgeService.SendStructuredMessageAsync(
                conversationId,
                ctx.Response.Response,
                "assistant",
                ctx.Response.QuickReplies,
                null,
                ctx.Response.AgentName,
                ctx.Response.AgentCompleted);

            actorLogger.Info($"Response sent successfully to client via SignalR (#quickReplies: {ctx.Response.QuickReplies?.Count ?? 0})");
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send SignalR message to client");

            // Attempt to send error notification to client
            try
            {
                await signalRBridgeService.SendStructuredMessageAsync(
                    conversationId,
                    "An error occurred while sending the response.",
                    "assistant",
                    null,
                    "delivery_error",
                    "Morgana",
                    false);
            }
            catch (Exception fallbackEx)
            {
                actorLogger.Error(fallbackEx, "Failed to send error notification to client");
            }
        }
    }

    /// <summary>
    /// Handles supervisor failures (timeout or exception via PipeTo).
    /// Sends an error message to the client via SignalR.
    /// </summary>
    /// <param name="failure">Failure information from supervisor</param>
    private async Task HandleSupervisorFailureAsync(Status.Failure failure)
    {
        actorLogger.Error(failure.Cause, "Supervisor did not reply in time or failed");

        // Send error message to client via SignalR
        try
        {
            await signalRBridgeService.SendStructuredMessageAsync(
                conversationId,
                "An internal error occurred.",
                "assistant",
                null,
                "supervisor_error",
                "Morgana",
                false);
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "Failed to send error message to client");
        }
    }

    /// <summary>
    /// Actor lifecycle hook: called when actor starts.
    /// </summary>
    protected override void PreStart()
    {
        actorLogger.Info($"ConversationManagerActor started for {conversationId}");
        base.PreStart();
    }

    /// <summary>
    /// Actor lifecycle hook: called when actor stops.
    /// </summary>
    protected override void PostStop()
    {
        actorLogger.Info($"ConversationManagerActor stopped for {conversationId}");
        base.PostStop();
    }
}