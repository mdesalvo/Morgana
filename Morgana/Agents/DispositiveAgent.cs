using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.Agents.Executors;
using Morgana.Messages;

namespace Morgana.Agents;

public class DispositiveAgent : MorganaAgent
{
    private readonly Dictionary<string, IActorRef> _executors = [];

    public DispositiveAgent(string conversationId, string userId) : base(conversationId, userId)
    {
        DependencyResolver? resolver = DependencyResolver.For(Context.System);
        _executors["contract_cancellation"] = Context.ActorOf(resolver.Props<ContractCancellationExecutorAgent>(conversationId, userId), $"cancellation-executor-{conversationId}");

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
            Sender.Tell(new ExecuteResponse("Mi dispiace, non posso gestire questa richiesta dispositiva al momento."));
            return;
        }

        ExecuteResponse? response = await executorAgent.Ask<ExecuteResponse>(req);
        originalSender.Tell(response);
    }
}