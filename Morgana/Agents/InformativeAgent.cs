using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.Agents.Executors;
using Morgana.Messages;

namespace Morgana.Agents;

public class InformativeAgent : ReceiveActor
{
    private readonly Dictionary<string, IActorRef> _executors = [];

    public InformativeAgent()
    {
        var resolver = DependencyResolver.For(Context.System);
        _executors["billing_retrieval"] = Context.ActorOf(resolver.Props<BillingExecutorAgent>(), "billing-executor");
        _executors["hardware_troubleshooting"] = Context.ActorOf(resolver.Props<HardwareTroubleshootingExecutorAgent>(), "hardware-executor");

        ReceiveAsync<ExecuteRequest>(RouteToExecutor);
    }

    private async Task RouteToExecutor(ExecuteRequest req)
    {
        var intent = req.Classification.Intent;
        var executor = _executors.ContainsKey(intent)
            ? _executors[intent]
            : _executors["billing_retrieval"]; // default

        var response = await executor.Ask<ExecuteResponse>(req, TimeSpan.FromSeconds(15));
        Sender.Tell(response);
    }
}