using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Actors;

/// <summary>
/// Content moderation actor that enforces guard-rail policies on every incoming user message.
/// </summary>
/// <remarks>
/// <para>
/// This actor is intentionally thin: all moderation logic is delegated to
/// <see cref="IGuardRailService"/>, which is resolved from DI and can be swapped
/// without touching the actor system. The default implementation
/// (<see cref="Services.LLMGuardRailService"/>) reproduces the two-level strategy
/// (fast profanity check + async LLM policy check) that was previously embedded here.
/// </para>
///
/// <para><strong>Tell Pattern:</strong></para>
/// <para>Captures the sender reference before the async call and replies directly via
/// <c>originalSender.Tell()</c>, avoiding temporary Ask actors and the associated
/// lifecycle overhead.</para>
///
/// <para><strong>Error Handling:</strong></para>
/// <para><see cref="IGuardRailService"/> implementations are expected to fail open on
/// transient errors (see interface contract). Should the service itself throw an unhandled
/// exception, this actor propagates a <see cref="Status.Failure"/> to the supervisor so
/// it can apply its own fail-open fallback.</para>
/// </remarks>
public class GuardActor : MorganaActor
{
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
        this.guardRailService = guardRailService;

        ReceiveAsync<Records.GuardCheckRequest>(CheckComplianceAsync);
    }

    /// <summary>
    /// Delegates the compliance check to <see cref="IGuardRailService"/> and replies to the sender.
    /// </summary>
    /// <param name="req">Guard check request containing the conversation ID and the message to evaluate.</param>
    private async Task CheckComplianceAsync(Records.GuardCheckRequest req)
    {
        IActorRef originalSender = Sender;

        try
        {
            bool enableGuardrail = configuration.GetValue("Morgana:ActorSystem:EnableGuardrail", true);
            Records.GuardRailResult result =
                enableGuardrail ? await guardRailService.CheckAsync(req.ConversationId, req.Message)
                                : new Records.GuardRailResult(true, null);

            actorLogger.Info(
                "Guard check complete for conversation {0}: compliant={1}",
                req.ConversationId, result.Compliant);

            // Map GuardRailResult → GuardCheckResponse (the message the supervisor expects)
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