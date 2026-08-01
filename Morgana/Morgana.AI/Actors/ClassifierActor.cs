using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Actors;

/// <summary>
/// Intent classification actor that analyses user messages and determines their underlying intent.
/// </summary>
/// <remarks>
/// Thin orchestration actor that delegates all classification logic to IClassifierService,
/// making the strategy swappable via DI. Default implementation (LLMClassifierService) uses
/// LLM-based classification with intent definitions and classifier prompt. Uses Tell pattern
/// (captures sender, replies via originalSender.Tell) to avoid Ask actor overhead.
/// IClassifierService implementations fail safe by returning "other" intent on transient errors;
/// unhandled exceptions propagate Status.Failure to supervisor for its fallback handling.
/// </remarks>
public class ClassifierActor : MorganaActor
{
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
        this.classifierService = classifierService;

        ReceiveAsync<Records.UserMessage>(ClassifyMessageAsync);
    }

    /// <summary>
    /// Delegates the classification to <see cref="IClassifierService"/> and replies to the sender.
    /// </summary>
    /// <param name="msg">User message to classify.</param>
    private async Task ClassifyMessageAsync(Records.UserMessage msg)
    {
        IActorRef originalSender = Sender;

        try
        {
            Records.ClassificationResult result =
                await classifierService.ClassifyAsync(msg.ConversationId, msg.Text);

            actorLogger.Info(
                "Classification complete for conversation {0}: intent='{1}', confidence={2}",
                msg.ConversationId,
                result.Intent,
                result.Metadata.GetValueOrDefault("confidence", "N/A"));

            originalSender.Tell(result);
        }
        catch (Exception ex)
        {
            actorLogger.Error(ex, "ClassifierActor: unexpected error during classification");

            // Propagate failure to supervisor — it will apply its own 'other'-intent fallback
            originalSender.Tell(new Status.Failure(ex));
        }
    }
}