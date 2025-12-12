using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Agents;
using static Morgana.Records;

namespace Morgana.Actors;

public class RouterActor : MorganaActor
{
    private readonly Dictionary<string, IActorRef> agents = [];

    public RouterActor(string conversationId) : base(conversationId)
    {
        DependencyResolver? dependencyResolver = DependencyResolver.For(Context.System);

        //Tutte le tipologie di agente registrate che devono poter lavorare in tandem con il classificatore
        agents["billing_retrieval"] = Context.ActorOf(dependencyResolver.Props<BillingAgent>(conversationId), $"billing-agent-{conversationId}");
        agents["hardware_troubleshooting"] = Context.ActorOf(dependencyResolver.Props<HardwareTroubleshootingAgent>(conversationId), $"hardware-agent-{conversationId}");
        agents["contract_cancellation"] = Context.ActorOf(dependencyResolver.Props<ContractCancellationAgent>(conversationId), $"contractcancellation-agent-{conversationId}");

        ReceiveAsync<ExecuteRequest>(RouteToAgentAsync);
    }

    private async Task RouteToAgentAsync(ExecuteRequest req)
    {
        IActorRef? senderRef = Sender;

        // Se non c’è classificazione → questo non è un turno valido per RouterActor
        if (req.Classification == null)
        {
            senderRef.Tell(new AgentResponse("Errore interno: classificazione mancante.", true));
            return;
        }

        if (!agents.TryGetValue(req.Classification.Intent, out IActorRef? agent))
        {
            senderRef.Tell(new AgentResponse("Mi dispiace,non sono ancora in grado di gestire l'intento di richiesta.", true));
            return;
        }

        // Chiede all'agente concreto
        AgentResponse? agentResponse = await agent.Ask<AgentResponse>(req);

        // Risponde al supervisore con il riferimento dell'agente reale
        senderRef.Tell(new InternalAgentResponse(
            agentResponse.Response,
            agentResponse.IsCompleted,
            agent
        ));
    }

}