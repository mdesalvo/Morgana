using Akka.Actor;
using Akka.DependencyInjection;
using static Morgana.Records;

namespace Morgana.Agents;

public class ConversationSupervisorAgent : MorganaAgent
{
    private readonly IActorRef classifierAgent;
    private readonly IActorRef informativeAgent;
    private readonly IActorRef dispositiveAgent;
    private readonly IActorRef guardAgent;
    private readonly ILogger<ConversationSupervisorAgent> logger;

    public ConversationSupervisorAgent(string conversationId, string userId, ILogger<ConversationSupervisorAgent> logger) : base(conversationId, userId)
    {
        this.logger = logger;

        DependencyResolver? dependencyResolver = DependencyResolver.For(Context.System);
        classifierAgent = Context.ActorOf(dependencyResolver.Props<ClassifierAgent>(conversationId, userId), $"classifier-{conversationId}");
        informativeAgent = Context.ActorOf(dependencyResolver.Props<InformativeAgent>(conversationId, userId), $"informative-{conversationId}");
        dispositiveAgent = Context.ActorOf(dependencyResolver.Props<DispositiveAgent>(conversationId, userId), $"dispositive-{conversationId}");
        guardAgent = Context.ActorOf(dependencyResolver.Props<GuardAgent>(conversationId, userId), $"guard-{conversationId}");

        ReceiveAsync<UserMessage>(HandleUserMessageAsync);
    }

    private async Task HandleUserMessageAsync(UserMessage msg)
    {
        IActorRef originalSender = Sender;

        try
        {
            // 1. Guardrail
            GuardCheckResponse? guardCheckResponse = await guardAgent.Ask<GuardCheckResponse>(new GuardCheckRequest(msg.UserId, msg.Text));
            if (!guardCheckResponse.Compliant)
            {
                ConversationResponse response = new ConversationResponse(
                    $"La prego di mantenere un tono professionale. {guardCheckResponse.Violation}",
                    "guard_violation",
                    []);
                originalSender.Tell(response);
                return;
            }

            // 2. Classification
            ClassificationResult? classificationResult = await classifierAgent.Ask<ClassificationResult>(msg);

            // 3. Route to appropriate agent
            IActorRef executorAgent = classificationResult.Category.ToLower() switch
            {
                "informative" => informativeAgent,
                "dispositive" => dispositiveAgent,
                _ => informativeAgent
            };
            ExecuteResponse? executeResponse = await executorAgent.Ask<ExecuteResponse>(
                new ExecuteRequest(msg.UserId, msg.ConversationId, msg.Text, classificationResult));

            // 4. Return response
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