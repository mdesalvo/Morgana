using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.Agents.Executors;
using static Morgana.Records;

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
        IActorRef? senderRef = Sender;

        if (req.Classification == null)
        {
            senderRef.Tell(new ExecuteResponse("Errore interno: classificazione mancante.", true));
            return;
        }

        if (!_executors.TryGetValue(req.Classification.Intent, out IActorRef? executor))
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