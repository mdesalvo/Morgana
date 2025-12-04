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
        var resolver = DependencyResolver.For(Context.System);
        _executors["contract_cancellation"] = Context.ActorOf(resolver.Props<ContractCancellationExecutorAgent>(), "cancellation-executor");

        ReceiveAsync<ExecuteRequest>(RouteToExecutor);
    }

    private async Task RouteToExecutor(ExecuteRequest req)
    {
        string intent = req.Classification.Intent;
        var executor = _executors.ContainsKey(intent)
            ? _executors[intent]
            : Self; // fallback

        if (executor == Self)
        {
            Sender.Tell(new ExecuteResponse("Mi dispiace, non posso gestire questa richiesta al momento."));
            return;
        }

        var response = await executor.Ask<ExecuteResponse>(req, TimeSpan.FromSeconds(15));
        Sender.Tell(response);
    }
}