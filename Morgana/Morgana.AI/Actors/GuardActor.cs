using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Actors;

/// <summary>The conversation's guard: it hands each user message to the guard-rail service and relays the verdict.</summary>
public class GuardActor : MorganaActor
{
    /// <summary>
    /// The service that holds the whole moderation strategy.
    /// The actor never inspects the message itself: it hands it over and relays the verdict.
    /// </summary>
    private readonly IGuardRailService guardRailService;

    /// <summary>Binds the guard to its conversation and to the guard-rail service.</summary>
    public GuardActor(
        string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IGuardRailService guardRailService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        this.guardRailService = guardRailService;

        // The supervisor sends every user message here before anything else reads it.
        ReceiveAsync<Records.GuardCheckRequest>(CheckComplianceAsync);
    }

    /// <summary>Judges the user message and replies to the supervisor with the verdict or with the failure.</summary>
    private async Task CheckComplianceAsync(Records.GuardCheckRequest req)
    {
        // The supervisor of the conversation, which waits for the verdict.
        IActorRef originalSender = Sender;

        try
        {
            // Morgana:ActorSystem:EnableUserGuardrail switches the check off, in which case every message is admitted
            // with a verdict that names the switch as its deciding layer.
            Records.GuardRailResult verdict =
                configuration.GetValue("Morgana:ActorSystem:EnableUserGuardrail", true)
                    ? await guardRailService.CheckUserMessageAsync(req.ConversationId, req.Message, req.TurnCancellation)
                    : new Records.GuardRailResult(true, null, Records.GuardVerdictSource.Disabled);

            actorLogger.Info(
                "Guard check complete for conversation {0}: compliant={1}",
                req.ConversationId, verdict.Compliant);

            // The supervisor receives the verdict whole, its deciding layer included.
            originalSender.Tell(verdict);
        }
        catch (OperationCanceledException) when (req.TurnCancellation.IsCancellationRequested)
        {
            // The supervisor has already moved on and a late failure would be read as the failure of the phase it is in now.
            actorLogger.Info("Guard check stopped for conversation {0}: the supervisor no longer waits for it", req.ConversationId);
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "GuardActor: unexpected error during compliance check");

            // The supervisor refuses the message that nobody could judge.
            originalSender.Tell(new Status.Failure(ex));
        }
    }
}