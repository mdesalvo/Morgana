using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Actors;

/// <summary>
/// Content moderation actor that enforces guard-rail policies on every incoming user message.
/// </summary>
public class GuardActor : MorganaActor
{
    /// <summary>
    /// Guard-rail service holding the whole moderation strategy: the actor never inspects the
    /// message itself, it only hands it over and relays the verdict back to the supervisor.
    /// </summary>
    private readonly IGuardRailService guardRailService;

    /// <summary>
    /// Initialises a new instance of <see cref="GuardActor"/>.
    /// </summary>
    /// <param name="conversationId">Unique identifier for this conversation.</param>
    /// <param name="llmService">LLM service (passed to base; not used directly here).</param>
    /// <param name="promptResolverService">Prompt resolver (passed to base; not used directly here).</param>
    /// <param name="guardRailService">Guard-rail service that encapsulates all content moderation logic.</param>
    /// <param name="configuration">Morgana configuration (layered by ASP.NET).</param>
    public GuardActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IGuardRailService guardRailService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        // The service owns the whole moderation strategy, so the actor stays a relay between the supervisor and it.
        this.guardRailService = guardRailService;

        // The supervisor asks for a verdict on every message before anything else reads it.
        ReceiveAsync<Records.GuardCheckRequest>(CheckComplianceAsync);
    }

    /// <summary>
    /// Delegates the compliance check to <see cref="IGuardRailService"/> and replies to the sender.
    /// </summary>
    /// <param name="req">Guard check request containing the conversation ID and the message to evaluate.</param>
    private async Task CheckComplianceAsync(Records.GuardCheckRequest req)
    {
        // The supervisor waits for the verdict: the sender is captured before the first await.
        IActorRef originalSender = Sender;

        try
        {
            // The guardrail strategy lives behind this one call, which is controlled by a feature flag
            // permitting its eventual switch-off.
            Records.GuardRailResult result =
                configuration.GetValue("Morgana:ActorSystem:EnableGuardrail", true)
                    ? await guardRailService.CheckAsync(req.ConversationId, req.Message)
                    : new Records.GuardRailResult(true, null);

            // The log line records the verdict so a rejected message can be traced to its check.
            actorLogger.Info(
                "Guard check complete for conversation {0}: compliant={1}",
                req.ConversationId, result.Compliant);

            // The supervisor continues to classification on a compliant verdict and rejects the message otherwise.
            originalSender.Tell(new Records.GuardCheckResponse(result.Compliant, result.Violation));
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "GuardActor: unexpected error during compliance check");

            // Propagate failure to supervisor — it will apply its own fail-open fallback
            originalSender.Tell(new Status.Failure(ex));
        }
    }
}