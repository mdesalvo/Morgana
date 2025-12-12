using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Agents;
using static Morgana.Records;

namespace Morgana.Actors;

public class InformativeActor : MorganaActor
{
    private readonly Dictionary<string, IActorRef> agents = [];

    public InformativeActor(string conversationId) : base(conversationId)
    {
        DependencyResolver? dependencyResolver = DependencyResolver.For(Context.System);
        agents["billing_retrieval"] = Context.ActorOf(dependencyResolver.Props<BillingAgent>(conversationId), $"billing-agent-{conversationId}");
        agents["hardware_troubleshooting"] = Context.ActorOf(dependencyResolver.Props<HardwareTroubleshootingAgent>(conversationId), $"hardware-agent-{conversationId}");

        ReceiveAsync<ExecuteRequest>(RouteToExecutorAsync);
    }

    private async Task RouteToExecutorAsync(ExecuteRequest req)
    {
        IActorRef? senderRef = Sender;

        // Se non c’è classificazione → questo non è un turno valido per InformativeActor
        if (req.Classification == null)
        {
            senderRef.Tell(new ExecuteResponse("Errore interno: classificazione mancante.", true));
            return;
        }

        if (!agents.TryGetValue(req.Classification.Intent, out IActorRef? executor))
        {
            senderRef.Tell(new ExecuteResponse("Intent non gestito.", true));
            return;
        }

        // Chiede all'executor concreto
        ExecuteResponse? execResp = await executor.Ask<ExecuteResponse>(req);

        // Risponde al supervisore con il riferimento dell'executor reale
        senderRef.Tell(new InternalExecuteResponse(
            execResp.Response,
            execResp.IsCompleted,
            executor
        ));
    }

}