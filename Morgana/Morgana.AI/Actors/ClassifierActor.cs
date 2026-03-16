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
/// <para>
/// This actor is intentionally thin: all classification logic is delegated to
/// <see cref="IClassifierService"/>, which is resolved from DI and can be swapped
/// without touching the actor system. The default implementation
/// (<see cref="Services.LLMClassifierService"/>) reproduces the LLM-based strategy
/// (intent definitions + classifier prompt) that was previously embedded here.
/// </para>
///
/// <para><strong>Tell Pattern:</strong></para>
/// <para>Captures the sender reference before the async call and replies directly via
/// <c>originalSender.Tell()</c>, avoiding temporary Ask actors and the associated
/// lifecycle overhead.</para>
///
/// <para><strong>Error Handling:</strong></para>
/// <para><see cref="IClassifierService"/> implementations are expected to fail safe on
/// transient errors, returning a fallback <c>"other"</c> intent (see interface contract).
/// Should the service itself throw an unhandled exception, this actor propagates a
/// <see cref="Status.Failure"/> to the supervisor, which will apply its own
/// <c>"other"</c>-intent fallback.</para>
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