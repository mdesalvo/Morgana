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
        DependencyResolver? dependencyResolver = DependencyResolver.For(Context.System);
        _executors["billing_retrieval"] = Context.ActorOf(dependencyResolver.Props<BillingExecutorAgent>(), "billing-executor");
        _executors["hardware_troubleshooting"] = Context.ActorOf(dependencyResolver.Props<HardwareTroubleshootingExecutorAgent>(), "hardware-executor");

        ReceiveAsync<ExecuteRequest>(RouteToExecutorAsync);
    }

    private async Task RouteToExecutorAsync(ExecuteRequest req)
    {
        IActorRef originalSender = Sender;

        string intent = req.Classification.Intent;
        IActorRef? executorAgent = _executors.ContainsKey(intent)
            ? _executors[intent]
            : Self; // fallback

        if (executorAgent.Equals(Self))
        {
            Sender.Tell(new ExecuteResponse("Mi dispiace, non posso gestire questa richiesta informativa al momento."));
            return;
        }

        ExecuteResponse? response = await executorAgent.Ask<ExecuteResponse>(req, TimeSpan.FromSeconds(15));
        originalSender.Tell(response);
    }
}