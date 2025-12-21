using Akka.Actor;
using Akka.Event;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
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
        IAgentRegistryService agentResolverService) : base(conversationId, llmService, promptResolverService)
    {
        //Autodiscovery of routable agents
        foreach (string intent in agentResolverService.GetAllIntents())
        {
            Type? agentType = agentResolverService.ResolveAgentFromIntent(intent);
            if (agentType != null)
                agents[intent] = Context.System.GetOrCreateAgent(agentType, intent, conversationId).GetAwaiter().GetResult();
        }

        ReceiveAsync<AgentRequest>(RouteToAgentAsync);
        Receive<BroadcastContextUpdate>(HandleBroadcastContextUpdate);
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

        // Risponde al supervisore con il riferimento dell'agente attivo
        senderRef.Tell(new ActiveAgentResponse(
            agentResponse.Response,
            agentResponse.IsCompleted,
            selectedAgent
        ));
    }

    private void HandleBroadcastContextUpdate(BroadcastContextUpdate msg)
    {
        Context.GetLogger().Info(
            $"RouterActor broadcasting context update from '{msg.SourceAgentIntent}': {string.Join(", ", msg.UpdatedValues.Keys)}");

        int broadcastCount = 0;
        
        // Broadcast a TUTTI gli agenti TRANNE il sender
        foreach (KeyValuePair<string, IActorRef> kvp in agents)
        {
            if (kvp.Key != msg.SourceAgentIntent)
            {
                kvp.Value.Tell(new ReceiveContextUpdate(
                    msg.SourceAgentIntent,
                    msg.UpdatedValues));

                broadcastCount++;
            }
        }

        Context.GetLogger().Info($"Context broadcast sent to {broadcastCount} agents (excluding sender '{msg.SourceAgentIntent}')");
    }
}