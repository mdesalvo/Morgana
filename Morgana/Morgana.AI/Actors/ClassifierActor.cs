using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Actors;

/// <summary>
/// Intent classification actor that analyses user messages and determines their underlying intent.
/// </summary>
public class ClassifierActor : MorganaActor
{
    /// <summary>
    /// Classifier service holding the whole classification strategy: the actor never reads the
    /// message itself, it only hands it over and relays the ranked result — collision metadata
    /// included — back to the supervisor.
    /// </summary>
    private readonly IClassifierService classifierService;

    /// <summary>
    /// Initialises a new instance of <see cref="ClassifierActor"/>.
    /// </summary>
    /// <param name="conversationId">Unique identifier for this conversation.</param>
    /// <param name="llmService">LLM service (passed to base; not used directly here).</param>
    /// <param name="promptResolverService">Prompt resolver (passed to base; not used directly here).</param>
    /// <param name="classifierService">
    /// Classifier service that encapsulates all intent classification logic.
    /// Injected by Akka DI from the ASP.NET Core service container.
    /// </param>
    /// <param name="configuration">Morgana configuration (layered by ASP.NET).</param>
    public ClassifierActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IClassifierService classifierService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        // The service owns the whole strategy, so the actor stays a relay between the supervisor and it.
        this.classifierService = classifierService;

        // The supervisor asks for the intent of each message that arrives while no agent is active.
        ReceiveAsync<Records.UserMessage>(ClassifyMessageAsync);
    }

    /// <summary>
    /// Delegates the classification to <see cref="IClassifierService"/> and replies to the sender.
    /// </summary>
    /// <param name="msg">User message to classify.</param>
    private async Task ClassifyMessageAsync(Records.UserMessage msg)
    {
        // The supervisor waits for the answer: the sender is captured before the first await.
        IActorRef originalSender = Sender;

        try
        {
            // The classification strategy (LLM call, ranking, the confidence-gap collision check
            // that decides whether metadata carries "ambiguousIntents") lives behind this one call.
            Records.ClassificationResult classificationResult =
                await classifierService.ClassifyAsync(msg.ConversationId, msg.Text);

            // The log line shows how sure the classifier was, which is what an operator reads when a message is misrouted.
            actorLogger.Info(
                "Classification complete for conversation {0}: intent='{1}', confidence={2}",
                msg.ConversationId,
                classificationResult.Intent,
                classificationResult.Metadata.GetValueOrDefault("confidence", "N/A"));

            // Replies the supervisor with the result of the classification, which includes the intents
            // ranked by descending score and the eventual collision metadata to support user-driven disambiguation.
            originalSender.Tell(classificationResult);
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "ClassifierActor: unexpected error during classification");

            // The supervisor owns the fallback to "other": the actor only reports that classification failed.
            originalSender.Tell(new Status.Failure(ex));
        }
    }
}