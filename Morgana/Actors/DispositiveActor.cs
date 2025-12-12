using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Agents;
using static Morgana.Records;

namespace Morgana.Actors;

public class DispositiveActor : MorganaActor
{
    private readonly Dictionary<string, IActorRef> agents = [];

    public DispositiveActor(string conversationId) : base(conversationId)
    {
        DependencyResolver? resolver = DependencyResolver.For(Context.System);
        agents["contract_cancellation"] = Context.ActorOf(resolver.Props<ContractCancellationAgent>(conversationId), $"cancellation-agent-{conversationId}");

        ReceiveAsync<ExecuteRequest>(RouteToExecutorAsync);
    }

    private async Task RouteToExecutorAsync(ExecuteRequest req)
    {
        IActorRef? senderRef = Sender;

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

        ExecuteResponse? execResp = await executor.Ask<ExecuteResponse>(req);

        senderRef.Tell(new InternalExecuteResponse(
            execResp.Response,
            execResp.IsCompleted,
            executor
        ));
    }

}