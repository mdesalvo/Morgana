using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.Messages;

namespace Morgana.Agents;

public class ConversationSupervisorAgent : ReceiveActor
{
    private readonly IActorRef _classifier;
    private readonly IActorRef _informative;
    private readonly IActorRef _dispositive;
    private readonly IActorRef _guard;
    private readonly IActorRef _archiver;
    private readonly ILogger<ConversationSupervisorAgent> _logger;

    public ConversationSupervisorAgent(ILogger<ConversationSupervisorAgent> logger)
    {
        _logger = logger;

        var resolver = DependencyResolver.For(Context.System);
        _classifier = Context.ActorOf(resolver.Props<ClassifierAgent>(), "classifier");
        _informative = Context.ActorOf(resolver.Props<InformativeAgent>(), "informative");
        _dispositive = Context.ActorOf(resolver.Props<DispositiveAgent>(), "dispositive");
        _guard = Context.ActorOf(resolver.Props<GuardAgent>(), "guard");
        _archiver = Context.ActorOf(resolver.Props<ArchiverAgent>(), "archiver");

        ReceiveAsync<UserMessage>(HandleUserMessage);
    }

    private async Task HandleUserMessage(UserMessage msg)
    {
        var originalSender = Sender;

        try
        {
            // 1. Guard check
            var guardResult = await _guard.Ask<GuardCheckResponse>(
                new GuardCheckRequest(msg.UserId, msg.Content), TimeSpan.FromSeconds(5));

            if (!guardResult.IsCompliant)
            {
                var response = new ConversationResponse(
                    $"La prego di mantenere un tono professionale. {guardResult.Violation}",
                    "guard_violation",
                    []);
                originalSender.Tell(response);
                return;
            }

            // 2. Classification
            var classification = await _classifier.Ask<ClassificationResult>(msg, TimeSpan.FromSeconds(10));

            // 3. Route to appropriate agent
            var executor = classification.Category.ToLower() switch
            {
                "informative" => _informative,
                "dispositive" => _dispositive,
                _ => _informative
            };

            var executeRequest = new ExecuteRequest(msg.UserId, msg.SessionId, msg.Content, classification);
            var executeResponse = await executor.Ask<ExecuteResponse>(executeRequest, TimeSpan.FromSeconds(20));

            // 4. Archive conversation
            _archiver.Tell(new ArchiveRequest(msg.UserId, msg.SessionId, msg.Content, executeResponse.Response, classification));

            // 5. Return response
            var finalResponse = new ConversationResponse(
                executeResponse.Response,
                classification.Category,
                classification.Metadata);
            originalSender.Tell(finalResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message");
            originalSender.Tell(new ConversationResponse(
                "Si è verificato un errore. La preghiamo di riprovare.",
                "error",
                []));
        }
    }
}