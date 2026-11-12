using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Actors;

/// <summary>The conversation's classifier: it hands each user message to the classifier service and relays the ranked intents.</summary>
public class ClassifierActor : MorganaActor
{
    /// <summary>
    /// The service that holds the whole classification strategy.
    /// The actor never reads the message itself: it hands it over and relays the result, collision metadata included.
    /// </summary>
    private readonly IClassifierService classifierService;

    /// <summary>Binds the classifier to its conversation and to the classifier service.</summary>
    public ClassifierActor(
        string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IClassifierService classifierService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        // The service arrives from the actor system's DI scope and lives as long as the actor.
        this.classifierService = classifierService;

        // The supervisor sends the user message here once the guard has admitted it.
        ReceiveAsync<Records.UserMessage>(ClassifyMessageAsync);
    }

    /// <summary>Classifies the user message and replies to the supervisor with the result or with the failure.</summary>
    private async Task ClassifyMessageAsync(Records.UserMessage msg)
    {
        // The supervisor is captured before the await, after which Sender no longer names it.
        IActorRef originalSender = Sender;

        try
        {
            // The classification strategy (LLM call, ranking, the confidence-gap collision check
            // that decides whether metadata carries "ambiguousIntents") lives behind this one call.
            Records.ClassificationResult classificationResult =
                await classifierService.ClassifyAsync(msg.ConversationId, msg.Text, msg.TurnCancellation);

            actorLogger.Info(
                "Classification complete for conversation {0}: intent='{1}', confidence={2}",
                msg.ConversationId,
                classificationResult.Intent,
                classificationResult.Metadata.GetValueOrDefault("confidence", "N/A"));

            // The supervisor receives the intents ranked by descending score, with the collision metadata that lets the user disambiguate.
            originalSender.Tell(classificationResult);
        }
        catch (OperationCanceledException) when (msg.TurnCancellation.IsCancellationRequested)
        {
            // The supervisor has already moved on and a late failure would be read as the failure of the phase it is in now.
            actorLogger.Info("Classification stopped for conversation {0}: the supervisor no longer waits for it", msg.ConversationId);
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "ClassifierActor: unexpected error during classification");

            // The supervisor reads the failure as a classification that found nothing and falls back to the Other intent.
            originalSender.Tell(new Status.Failure(ex));
        }
    }
}