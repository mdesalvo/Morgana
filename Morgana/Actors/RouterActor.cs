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
        IAgentRegistryService agentResolverService,
        IMCPToolProvider? mcpToolProvider=null) : base(conversationId, llmService, promptResolverService)
    {
        // Autodiscovery of routable agents
        foreach (string intent in agentResolverService.GetAllIntents())
        {
            Type? agentType = agentResolverService.ResolveAgentFromIntent(intent);
            if (agentType != null)
                agents[intent] = Context.System.GetOrCreateAgent(agentType, intent, conversationId, mcpToolProvider).GetAwaiter().GetResult();
        }

        ReceiveAsync<AgentRequest>(RouteToAgentAsync);
        Receive<Records.AgentResponseContext>(HandleAgentResponse);
        Receive<Status.Failure>(HandleFailure);
        Receive<BroadcastContextUpdate>(HandleBroadcastContextUpdate);
    }

    private async Task RouteToAgentAsync(AgentRequest req)
    {
        IActorRef originalSender = Sender;
        Prompt classifierPrompt = await promptResolverService.ResolveAsync("Classifier");

        if (req.Classification == null)
        {
            originalSender.Tell(new AgentResponse(
                classifierPrompt.GetAdditionalProperty<string>("MissingClassificationError"), true));
            return;
        }

        if (!agents.TryGetValue(req.Classification.Intent, out IActorRef? selectedAgent))
        {
            originalSender.Tell(new AgentResponse(
                classifierPrompt.GetAdditionalProperty<string>("UnrecognizedIntentError"), true));
            return;
        }

        actorLogger.Info($"Routing intent '{req.Classification.Intent}' to agent {selectedAgent.Path}");

        selectedAgent.Ask<AgentResponse>(req, TimeSpan.FromSeconds(60))
            .PipeTo(Self, 
                success: response => new Records.AgentResponseContext(response, selectedAgent, originalSender),
                failure: ex => new Status.Failure(ex));
    }

    private void HandleAgentResponse(Records.AgentResponseContext ctx)
    {
        actorLogger.Info($"Received response from agent {ctx.AgentRef.Path}, completed: {ctx.Response.IsCompleted}");

        ctx.OriginalSender.Tell(new ActiveAgentResponse(
            ctx.Response.Response,
            ctx.Response.IsCompleted,
            ctx.AgentRef
        ));
    }

    private void HandleFailure(Status.Failure failure)
    {
        actorLogger.Error(failure.Cause, "Agent routing failed");
        
        Prompt classifierPrompt = promptResolverService.ResolveAsync("Classifier").GetAwaiter().GetResult();
        Sender.Tell(new AgentResponse(
            classifierPrompt.GetAdditionalProperty<string>("UnrecognizedIntentError"), 
            true));
    }

    private void HandleBroadcastContextUpdate(BroadcastContextUpdate msg)
    {
        actorLogger.Info($"Broadcasting context from '{msg.SourceAgentIntent}': {string.Join(", ", msg.UpdatedValues.Keys)}");

        int broadcastCount = 0;
        
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

        actorLogger.Info($"Context broadcast sent to {broadcastCount} agents");
    }
}