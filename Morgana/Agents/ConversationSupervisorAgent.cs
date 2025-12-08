using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.Messages;

namespace Morgana.Agents;

public class ConversationSupervisorAgent : ReceiveActor
{
    private readonly IActorRef classifierAgent;
    private readonly IActorRef informativeAgent;
    private readonly IActorRef dispositiveAgent;
    private readonly IActorRef guardAgent;
    private readonly IActorRef archiverAgent;
    private readonly ILogger<ConversationSupervisorAgent> logger;

    public ConversationSupervisorAgent(ILogger<ConversationSupervisorAgent> logger)
    {
        this.logger = logger;

        DependencyResolver? dependencyResolver = DependencyResolver.For(Context.System);
        classifierAgent = Context.ActorOf(dependencyResolver.Props<ClassifierAgent>(), "classifier");
        informativeAgent = Context.ActorOf(dependencyResolver.Props<InformativeAgent>(), "informative");
        dispositiveAgent = Context.ActorOf(dependencyResolver.Props<DispositiveAgent>(), "dispositive");
        guardAgent = Context.ActorOf(dependencyResolver.Props<GuardAgent>(), "guard");
        archiverAgent = Context.ActorOf(dependencyResolver.Props<ArchiverAgent>(), "archiver");

        ReceiveAsync<UserMessage>(HandleUserMessage);
        
    }

    private async Task HandleUserMessage(UserMessage msg)
    {
        IActorRef? originalSender = Sender;

        try
        {
            // 1. Guard check
            GuardCheckResponse? guardCheckResponse = await guardAgent.Ask<GuardCheckResponse>(
                new GuardCheckRequest(msg.UserId, msg.Text), TimeSpan.FromSeconds(5));
            if (!guardCheckResponse.IsCompliant)
            {
                ConversationResponse response = new ConversationResponse(
                    $"La prego di mantenere un tono professionale. {guardCheckResponse.Violation}",
                    "guard_violation",
                    []);
                originalSender.Tell(response);
                return;
            }

            // 2. Classification
            ClassificationResult? classificationResult = await classifierAgent.Ask<ClassificationResult>(msg, TimeSpan.FromSeconds(10));

            // 3. Route to appropriate agent
            IActorRef executorAgent = classificationResult.Category.ToLower() switch
            {
                "informative" => informativeAgent,
                "dispositive" => dispositiveAgent,
                _ => informativeAgent
            };
            ExecuteResponse? executeResponse = await executorAgent.Ask<ExecuteResponse>(
                new ExecuteRequest(msg.UserId, msg.ConversationId, msg.Text, classificationResult), TimeSpan.FromSeconds(20));

            // 4. Archive conversation
            archiverAgent.Tell(new ArchiveRequest(msg.UserId, msg.ConversationId, msg.Text, executeResponse.Response, classificationResult));

            // 5. Return response
            originalSender.Tell(new ConversationResponse(
                executeResponse.Response,
                classificationResult.Category,
                classificationResult.Metadata));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing message");
            originalSender.Tell(new ConversationResponse(
                "Si è verificato un errore. La preghiamo di riprovare.",
                "error",
                []));
        }
    }
}