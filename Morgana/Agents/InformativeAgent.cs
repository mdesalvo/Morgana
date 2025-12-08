using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.Agents.Executors;
using Morgana.Messages;

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
        IActorRef originalSender = Sender;

        IActorRef? executorAgent = _executors.ContainsKey(req.Classification.Intent)
            ? _executors[req.Classification.Intent]
            : Self; // fallback

        if (executorAgent.Equals(Self))
        {
            Sender.Tell(new ExecuteResponse("Mi dispiace, non posso gestire questa richiesta informativa al momento."));
            return;
        }

        ExecuteResponse? response = await executorAgent.Ask<ExecuteResponse>(req);
        originalSender.Tell(response);
    }
}