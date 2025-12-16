using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Abstractions;
using Morgana.AI.Agents;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

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
        agents["billing"] = Context.ActorOf(dependencyResolver.Props<BillingAgent>(conversationId), $"billing-agent-{conversationId}");
        agents["contract"] = Context.ActorOf(dependencyResolver.Props<ContractAgent>(conversationId), $"contract-agent-{conversationId}");
        agents["troubleshooting"] = Context.ActorOf(dependencyResolver.Props<TroubleshootingAgent>(conversationId), $"troubleshooting-agent-{conversationId}");

        ReceiveAsync<AI.Records.AgentRequest>(RouteToAgentAsync);
    }

    private async Task RouteToAgentAsync(AI.Records.AgentRequest req)
    {
        IActorRef? senderRef = Sender;
        Prompt classifierPrompt = await promptResolverService.ResolveAsync("Classifier");

        // Se non c’è classificazione → questo non è un turno valido per RouterActor
        if (req.Classification == null)
        {
            senderRef.Tell(new AI.Records.AgentResponse("Errore interno: classificazione mancante.", true));
            return;
        }

        if (!agents.TryGetValue(req.Classification.Intent, out IActorRef? agent))
        {
            senderRef.Tell(new AI.Records.AgentResponse(classifierPrompt.GetAdditionalProperty<string>("UnrecognizedIntentAnswer"), true));
            return;
        }

        // Chiede all'agente concreto
        AI.Records.AgentResponse? agentResponse = await agent.Ask<AI.Records.AgentResponse>(req);

        // Risponde al supervisore con il riferimento dell'agente reale
        senderRef.Tell(new AI.Records.InternalAgentResponse(
            agentResponse.Response,
            agentResponse.IsCompleted,
            agent
        ));
    }

}