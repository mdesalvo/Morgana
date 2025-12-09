using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.Agents.Executors;
using static Morgana.Records;

namespace Morgana.Agents;

public class InformativeAgent : MorganaAgent
{
    private readonly Dictionary<string, IActorRef> _executors = [];

    public InformativeAgent(string conversationId, string userId) : base(conversationId, userId)
    {
        DependencyResolver? dependencyResolver = DependencyResolver.For(Context.System);
        _executors["billing_retrieval"] = Context.ActorOf(dependencyResolver.Props<BillingExecutorAgent>(conversationId, userId), $"billing-executor-{conversationId}");
        _executors["hardware_troubleshooting"] = Context.ActorOf(dependencyResolver.Props<HardwareTroubleshootingExecutorAgent>(conversationId, userId), $"hardware-executor-{conversationId}");

        ReceiveAsync<ExecuteRequest>(RouteToExecutorAsync);
    }

    private async Task RouteToExecutorAsync(ExecuteRequest req)
    {
        IActorRef? originalSender = Sender;

        // Se non c’è classificazione → questo non è un turno valido per InformativeAgent
        if (req.Classification == null)
        {
            originalSender.Tell(new ExecuteResponse("Errore interno: classificazione mancante.", true));
            return;
        }

        if (!_executors.TryGetValue(req.Classification.Intent, out IActorRef? executor))
        {
            originalSender.Tell(new ExecuteResponse("Intent non gestito.", true));
            return;
        }

        // Chiede all'executor concreto
        ExecuteResponse? execResp = await executor.Ask<ExecuteResponse>(req);

        // Risponde al supervisore con il riferimento dell'executor reale
        originalSender.Tell(new InternalExecuteResponse(
            execResp.Response,
            execResp.IsCompleted,
            executor
        ));
    }

}