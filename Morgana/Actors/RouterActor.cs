using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Abstractions;
using Morgana.AI.Agents;
using Morgana.AI.Interfaces;

namespace Morgana.Actors;

public class RouterActor : MorganaActor
{
    private readonly IPromptResolverService promptResolverService;
    private readonly Dictionary<string, IActorRef> agents = [];

    public RouterActor(
        string conversationId,
        IPromptResolverService promptResolverService) : base(conversationId)
    {
        this.promptResolverService = promptResolverService;

        //Tutte le tipologie di agente registrate che devono poter lavorare in tandem con il classificatore
        DependencyResolver? dependencyResolver = DependencyResolver.For(Context.System);
        agents["billing"] = Context.ActorOf(dependencyResolver.Props<BillingAgent>(conversationId, promptResolverService), $"billing-agent-{conversationId}");
        agents["contract"] = Context.ActorOf(dependencyResolver.Props<ContractAgent>(conversationId, promptResolverService), $"contract-agent-{conversationId}");
        agents["troubleshooting"] = Context.ActorOf(dependencyResolver.Props<TroubleshootingAgent>(conversationId, promptResolverService), $"troubleshooting-agent-{conversationId}");

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