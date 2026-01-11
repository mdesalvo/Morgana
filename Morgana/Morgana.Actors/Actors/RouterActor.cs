using Akka.Actor;
using Akka.Event;
using Morgana.Actors.Abstractions;
using Morgana.Actors.Extensions;
using Morgana.Foundations;
using Morgana.Foundations.Interfaces;

namespace Morgana.Actors.Actors;

/// <summary>
/// Intent-to-agent routing actor that directs requests to specialized agents based on intent classification.
/// Maintains a registry of intent-to-agent mappings and handles agent discovery at startup.
/// Also manages cross-agent context broadcasting for shared state updates.
/// </summary>
/// <remarks>
/// This actor uses autodiscovery to load all available agents from the agent registry.
/// It routes requests using Ask pattern with PipeTo for non-blocking communication.
/// Supports broadcasting context updates from one agent to all other registered agents.
/// </remarks>
public class RouterActor : MorganaActor
{
    /// <summary>
    /// Dictionary mapping intent names to their corresponding agent actor references.
    /// Populated during actor initialization through autodiscovery.
    /// </summary>
    private readonly Dictionary<string, IActorRef> agents = [];

    /// <summary>
    /// Initializes a new instance of the RouterActor.
    /// Performs autodiscovery of all routable agents from the agent registry.
    /// </summary>
    /// <param name="conversationId">Unique identifier for this conversation</param>
    /// <param name="llmService">LLM service for AI completions</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="agentResolverService">Service for agent discovery and resolution</param>
    public RouterActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IAgentRegistryService agentResolverService) : base(conversationId, llmService, promptResolverService)
    {
        // Autodiscovery of routable agents
        foreach (string intent in agentResolverService.GetAllIntents())
        {
            Type? agentType = agentResolverService.ResolveAgentFromIntent(intent);
            if (agentType != null)
                agents[intent] = Context.System.GetOrCreateAgent(agentType, intent, conversationId).GetAwaiter().GetResult();
        }

        // Route classified requests to specialized agents based on intent:
        // - Validates classification exists and intent is recognized
        // - Forwards request to appropriate agent (BillingAgent, ContractAgent, etc.) using Ask pattern
        // - Wraps agent response with agent reference and completion status
        // - Returns error messages for missing/unrecognized intents
        ReceiveAsync<Records.AgentRequest>(RouteToAgentAsync);
        Receive<Records.AgentResponseContext>(HandleAgentResponse);
        Receive<Status.Failure>(HandleFailure);
        
        // Broadcast context updates from one agent to all other registered agents:
        // - Used for sharing context variables across agents (e.g., userId from BillingAgent → ContractAgent)
        // - Excludes source agent from broadcast to avoid self-notification
        Receive<Records.BroadcastContextUpdate>(HandleBroadcastContextUpdate);
    }

    /// <summary>
    /// Routes an agent request to the appropriate specialized agent based on intent classification.
    /// Uses Ask pattern with PipeTo for non-blocking communication.
    /// </summary>
    /// <param name="req">Agent request containing classification and message data</param>
    /// <remarks>
    /// Returns error messages for missing classification or unrecognized intents.
    /// Captures original sender before async operations to ensure correct response routing.
    /// Includes 60-second timeout for agent processing.
    /// </remarks>
    private async Task RouteToAgentAsync(Records.AgentRequest req)
    {
        IActorRef originalSender = Sender;
        Records.Prompt classifierPrompt = await promptResolverService.ResolveAsync("Classifier");

        // Validate classification exists
        if (req.Classification == null)
        {
            originalSender.Tell(new Records.AgentResponse(
                classifierPrompt.GetAdditionalProperty<string>("MissingClassificationError"), true));
            return;
        }

        // Validate intent is recognized
        if (!agents.TryGetValue(req.Classification.Intent, out IActorRef? selectedAgent))
        {
            originalSender.Tell(new Records.AgentResponse(
                classifierPrompt.GetAdditionalProperty<string>("UnrecognizedIntentError"), true));
            return;
        }

        actorLogger.Info($"Routing intent '{req.Classification.Intent}' to agent {selectedAgent.Path}");

        // Route to agent using Ask pattern with PipeTo
        _ = selectedAgent
                .Ask<Records.AgentResponse>(req, TimeSpan.FromSeconds(60))
                .PipeTo(
                    Self,
                    success: response => new Records.AgentResponseContext(response, selectedAgent, originalSender),
                    failure: ex => new Status.Failure(ex));
    }

    /// <summary>
    /// Handles successful responses from specialized agents (via PipeTo).
    /// Wraps the response with agent reference and completion status before forwarding to original sender.
    /// </summary>
    /// <param name="ctx">Context containing agent response, agent reference, and original sender</param>
    private void HandleAgentResponse(Records.AgentResponseContext ctx)
    {
        actorLogger.Info($"Received response from agent {ctx.AgentRef.Path}, " +
                         $"completed: {ctx.Response.IsCompleted}, " +
                         $"#quickReplies: {ctx.Response.QuickReplies?.Count ?? 0}");

        ctx.OriginalSender.Tell(new Records.ActiveAgentResponse(
            ctx.Response.Response,
            ctx.Response.IsCompleted,
            ctx.AgentRef,
            ctx.Response.QuickReplies));
    }

    /// <summary>
    /// Handles failures during agent routing or processing (via PipeTo).
    /// Returns an error message to the sender using the unrecognized intent error template.
    /// </summary>
    /// <param name="failure">Failure information</param>
    private void HandleFailure(Status.Failure failure)
    {
        actorLogger.Error(failure.Cause, "Agent routing failed");

        Records.Prompt classifierPrompt = promptResolverService.ResolveAsync("Classifier").GetAwaiter().GetResult();
        Sender.Tell(new Records.AgentResponse(
            classifierPrompt.GetAdditionalProperty<string>("UnrecognizedIntentError"), true));
    }

    /// <summary>
    /// Handles context update broadcasts from one agent to all other registered agents.
    /// Used for sharing context variables across agents (e.g., userId shared between billing and contract agents).
    /// </summary>
    /// <param name="msg">Broadcast message containing source intent and updated context values</param>
    /// <remarks>
    /// Excludes the source agent from the broadcast to avoid self-notification.
    /// Logs the number of agents that received the update.
    /// </remarks>
    private void HandleBroadcastContextUpdate(Records.BroadcastContextUpdate msg)
    {
        actorLogger.Info($"Broadcasting context from '{msg.SourceAgentIntent}': {string.Join(", ", msg.UpdatedValues.Keys)}");

        int broadcastCount = 0;

        // Broadcast to all agents except the source
        foreach (KeyValuePair<string, IActorRef> kvp in agents)
        {
            if (kvp.Key != msg.SourceAgentIntent)
            {
                kvp.Value.Tell(new Records.ReceiveContextUpdate(
                    msg.SourceAgentIntent,
                    msg.UpdatedValues));

                broadcastCount++;
            }
        }

        actorLogger.Info($"Context broadcast sent to {broadcastCount} agents");
    }
}