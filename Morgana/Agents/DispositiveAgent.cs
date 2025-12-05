using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.Agents.Executors;
using Morgana.Messages;

namespace Morgana.Agents;

public class DispositiveAgent : ReceiveActor
{
    private readonly Dictionary<string, IActorRef> _executors = [];

    public DispositiveAgent()
    {
        DependencyResolver? resolver = DependencyResolver.For(Context.System);
        _executors["contract_cancellation"] = Context.ActorOf(resolver.Props<ContractCancellationExecutorAgent>(), "cancellation-executor");

        ReceiveAsync<ExecuteRequest>(RouteToExecutor);
    }

    private async Task RouteToExecutor(ExecuteRequest req)
    {
        string intent = req.Classification.Intent;
        IActorRef? executorAgent = _executors.ContainsKey(intent)
            ? _executors[intent]
            : Self; // fallback

        if (executorAgent.Equals(Self))
        {
            Sender.Tell(new ExecuteResponse("Mi dispiace, non posso gestire questa richiesta dispositiva al momento."));
            return;
        }

        ExecuteResponse? response = await executorAgent.Ask<ExecuteResponse>(req, TimeSpan.FromSeconds(15));
        Sender.Tell(response);
    }
}