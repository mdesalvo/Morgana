using Akka.Actor;
using Akka.DependencyInjection;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.Actors;

public class RouterActor : MorganaActor
{
    private readonly Dictionary<string, IActorRef> agents = [];

    public RouterActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IAgentRegistryService agentRegistryService) : base(conversationId, llmService, promptResolverService)
    {
        DependencyResolver? dependencyResolver = DependencyResolver.For(Context.System);
        foreach (string intent in agentRegistryService.GetRegisteredIntents())
        {
            Type? agentType = agentRegistryService.GetAgentType(intent);
            if (agentType != null)
            {
                Props props = dependencyResolver.Props(agentType, conversationId);
                agents[intent] = Context.ActorOf(props, $"{intent}-{conversationId}");
            }
        }

        ReceiveAsync<AgentRequest>(RouteToAgentAsync);
    }

    private async Task RouteToAgentAsync(AgentRequest req)
    {
        IActorRef? senderRef = Sender;
        Prompt classifierPrompt = await promptResolverService.ResolveAsync("Classifier");

        // Se non c’è classificazione → questo non è un turno valido per RouterActor
        if (req.Classification == null)
        {
            senderRef.Tell(new AgentResponse(classifierPrompt.GetAdditionalProperty<string>("MissingClassificationError"), true));
            return;
        }

        if (!agents.TryGetValue(req.Classification.Intent, out IActorRef? selectedAgent))
        {
            senderRef.Tell(new AgentResponse(classifierPrompt.GetAdditionalProperty<string>("UnrecognizedIntentError"), true));
            return;
        }

        // Chiede all'agente selezionato
        AgentResponse? agentResponse = await selectedAgent.Ask<AgentResponse>(req);

        // Risponde al supervisore con il riferimento dell'agente reale
        senderRef.Tell(new InternalAgentResponse(
            agentResponse.Response,
            agentResponse.IsCompleted,
            selectedAgent
        ));
    }
}