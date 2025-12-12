using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Abstractions;
using Morgana.AI.Agents;

namespace Morgana.Actors;

public class RouterActor : MorganaActor
{
    private readonly Dictionary<string, IActorRef> agents = [];

    public RouterActor(string conversationId) : base(conversationId)
    {
        DependencyResolver? dependencyResolver = DependencyResolver.For(Context.System);

        //Tutte le tipologie di agente registrate che devono poter lavorare in tandem con il classificatore
        agents["billing"] = Context.ActorOf(dependencyResolver.Props<BillingAgent>(conversationId), $"billing-agent-{conversationId}");
        agents["contract"] = Context.ActorOf(dependencyResolver.Props<ContractAgent>(conversationId), $"contract-agent-{conversationId}");
        agents["troubleshooting"] = Context.ActorOf(dependencyResolver.Props<TroubleshootingAgent>(conversationId), $"troubleshooting-agent-{conversationId}");

        ReceiveAsync<Morgana.AI.Records.AgentRequest>(RouteToAgentAsync);
    }

    private async Task RouteToAgentAsync(Morgana.AI.Records.AgentRequest req)
    {
        IActorRef? senderRef = Sender;

        // Se non c’è classificazione → questo non è un turno valido per RouterActor
        if (req.Classification == null)
        {
            senderRef.Tell(new Morgana.AI.Records.AgentResponse("Errore interno: classificazione mancante.", true));
            return;
        }

        if (!agents.TryGetValue(req.Classification.Intent, out IActorRef? agent))
        {
            senderRef.Tell(new Morgana.AI.Records.AgentResponse("Mi dispiace,non sono ancora in grado di gestire questo tipo di richiesta.", true));
            return;
        }

        // Chiede all'agente concreto
        Morgana.AI.Records.AgentResponse? agentResponse = await agent.Ask<Morgana.AI.Records.AgentResponse>(req);

        // Risponde al supervisore con il riferimento dell'agente reale
        senderRef.Tell(new Morgana.AI.Records.InternalAgentResponse(
            agentResponse.Response,
            agentResponse.IsCompleted,
            agent
        ));
    }

}