using Akka.Actor;
using Akka.Event;
using Morgana.Framework.Abstractions;
using Morgana.Framework.Extensions;
using Morgana.Framework.Interfaces;

namespace Morgana.Framework.Actors;

/// <summary>
/// Intent-to-agent routing actor that directs requests to specialized agents based on intent classification.
/// Maintains a registry of intent-to-agent mappings with lazy agent creation.
/// Also manages cross-agent context broadcasting for shared state updates.
/// </summary>
/// <remarks>
/// This actor uses on-demand agent creation instead of upfront creation to avoid conflicts
/// during conversation resume when agents may already exist.
/// It routes requests using Ask pattern with PipeTo for non-blocking communication.
/// Supports broadcasting context updates from one agent to all other registered agents.
/// </remarks>
public class RouterActor : MorganaActor
{
    /// <summary>
    /// Dictionary mapping intent names to their corresponding agent actor references.
    /// Populated lazily on first use of each agent.
    /// </summary>
    private readonly Dictionary<string, IActorRef> agents = [];

    /// <summary>
    /// Dictionary mapping agent references to their original senders for streaming chunk forwarding.
    /// Populated when a request is routed to an agent, cleaned up when response is received.
    /// </summary>
    private readonly Dictionary<IActorRef, IActorRef> streamingContexts = [];

    /// <summary>
    /// Service for discovering agent types from intent names.
    /// </summary>
    private readonly IAgentRegistryService agentResolverService;

    /// <summary>
    /// Initializes a new instance of the RouterActor.
    /// Does NOT pre-create agents - they are created on-demand when first needed.
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
        this.agentResolverService = agentResolverService;

        // Route classified requests to specialized agents based on intent:
        // - Validates classification exists and intent is recognized
        // - Creates agent on-demand if not yet created
        // - Forwards request to appropriate agent (BillingAgent, ContractAgent, etc.) using Tell
        // - Receives both streaming chunks and final response via Tell
        // - Returns error messages for missing/unrecognized intents
        ReceiveAsync<Records.AgentRequest>(RouteToAgentAsync);
        Receive<Records.AgentResponse>(HandleAgentResponseDirect);
        
        // Forward streaming chunks from agents to supervisor
        Receive<Records.AgentStreamChunk>(HandleAgentStreamChunk);

        // Broadcast context updates from one agent to all other registered agents:
        // - Used for sharing context variables across agents (e.g., userId from BillingAgent → ContractAgent)
        // - Excludes source agent from broadcast to avoid self-notification
        // - Creates agents on-demand if they don't exist yet
        ReceiveAsync<Records.BroadcastContextUpdate>(HandleBroadcastContextUpdate);

        // Handle agent restoration requests from supervisor
        ReceiveAsync<Records.RestoreAgentRequest>(HandleRestoreAgentRequestAsync);
    }

    /// <summary>
    /// Gets or creates an agent for the specified intent.
    /// Uses lazy creation pattern to avoid conflicts during conversation resume.
    /// </summary>
    /// <param name="intent">Intent name (e.g., "billing", "contract")</param>
    /// <returns>Agent actor reference, or null if no agent handles this intent</returns>
    private async Task<IActorRef?> GetOrCreateAgentForIntent(string intent)
    {
        // Check if agent already created and cached
        if (agents.TryGetValue(intent, out IActorRef? cachedAgent))
        {
            actorLogger.Info($"Using cached agent for intent '{intent}': {cachedAgent.Path}");
            return cachedAgent;
        }

        // Resolve agent type from registry
        Type? agentType = agentResolverService.ResolveAgentFromIntent(intent);
        if (agentType == null)
        {
            actorLogger.Warning($"No agent type found for intent '{intent}'");
            return null;
        }

        // Create agent (or get if already exists - handles resume scenario)
        IActorRef agent = await Context.System.GetOrCreateAgent(agentType, intent, conversationId);

        // Cache for future requests
        agents[intent] = agent;

        actorLogger.Info($"Agent created/resolved for intent '{intent}': {agent.Path}");
        return agent;
    }

    /// <summary>
    /// Routes an agent request to the appropriate specialized agent based on intent classification.
    /// Creates agent on-demand if not yet created.
    /// Uses Ask pattern with PipeTo for non-blocking communication, while streaming chunks flow separately.
    /// </summary>
    /// <param name="req">Agent request containing classification and message data</param>
    /// <remarks>
    /// Returns error messages for missing classification or unrecognized intents.
    /// Captures original sender before async operations to ensure correct response routing.
    /// Streaming chunks arrive via separate Tell messages and are forwarded to original sender.
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

        // Get or create agent for this intent
        IActorRef? selectedAgent = await GetOrCreateAgentForIntent(req.Classification.Intent);

        // Validate agent exists for this intent
        if (selectedAgent == null)
        {
            originalSender.Tell(new Records.AgentResponse(
                classifierPrompt.GetAdditionalProperty<string>("UnrecognizedIntentError"), true));
            return;
        }

        actorLogger.Info($"Routing intent '{req.Classification.Intent}' to agent {selectedAgent.Path}");

        // Store streaming context for chunk and response forwarding
        streamingContexts[selectedAgent] = originalSender;

        // Route to agent using Tell (not Ask) to support streaming
        // Both chunks and final response will arrive via Tell and be handled separately
        selectedAgent.Tell(req);
    }

    /// <summary>
    /// Handles agent responses (final message after streaming completes).
    /// Wraps the response with agent reference and forwards to original sender.
    /// </summary>
    /// <param name="response">Agent response from specialized agent</param>
    private void HandleAgentResponseDirect(Records.AgentResponse response)
    {
        IActorRef agentSender = Sender;
        
        if (streamingContexts.TryGetValue(agentSender, out IActorRef? originalSender))
        {
            actorLogger.Info($"Received response from agent {agentSender.Path}, " +
                             $"completed: {response.IsCompleted}, " +
                             $"#quickReplies: {response.QuickReplies?.Count ?? 0}");

            // Clean up streaming context (response received, streaming done)
            streamingContexts.Remove(agentSender);

            // Forward to supervisor wrapped as ActiveAgentResponse
            originalSender.Tell(new Records.ActiveAgentResponse(
                response.Response,
                response.IsCompleted,
                agentSender,
                response.QuickReplies));
        }
        else
        {
            actorLogger.Warning($"Received response from unknown agent {agentSender.Path}");
        }
    }

    /// <summary>
    /// Handles context update broadcasts from one agent to all other registered agents.
    /// Creates agents on-demand if they don't exist yet.
    /// Used for sharing context variables across agents (e.g., userId shared between billing and contract agents).
    /// </summary>
    /// <param name="msg">Broadcast message containing source intent and updated context values</param>
    /// <remarks>
    /// Excludes the source agent from the broadcast to avoid self-notification.
    /// Logs the number of agents that received the update.
    /// </remarks>
    private async Task HandleBroadcastContextUpdate(Records.BroadcastContextUpdate msg)
    {
        actorLogger.Info($"Broadcasting context update from '{msg.SourceAgentIntent}': {string.Join(", ", msg.UpdatedValues.Keys)}");

        int broadcastCount = 0;

        // Get all registered intents and create/get agents for each
        foreach (string intent in agentResolverService.GetAllIntents())
        {
            // Skip source agent
            if (string.Equals(intent, msg.SourceAgentIntent, StringComparison.OrdinalIgnoreCase))
                continue;

            // Get or create agent for this intent
            IActorRef? agent = await GetOrCreateAgentForIntent(intent);

            if (agent != null)
            {
                agent.Tell(new Records.ReceiveContextUpdate(msg.SourceAgentIntent, msg.UpdatedValues));
                broadcastCount++;
            }
        }

        actorLogger.Info($"Context update broadcast complete: {broadcastCount} agent(s) notified");
    }

    /// <summary>
    /// Handles streaming chunks from agents and forwards them to the original sender (supervisor).
    /// Uses streamingContexts map to find the correct destination.
    /// Enables real-time progressive response rendering.
    /// </summary>
    /// <param name="chunk">Streaming chunk from agent</param>
    private void HandleAgentStreamChunk(Records.AgentStreamChunk chunk)
    {
        // Find the original sender for this agent's stream
        IActorRef agentSender = Sender;
        
        if (streamingContexts.TryGetValue(agentSender, out IActorRef? originalSender))
        {
            // Forward chunk to original sender (supervisor)
            // No logging to avoid spamming logs with partial text
            originalSender.Tell(chunk);
        }
        else
        {
            // Fallback: forward to parent (supervisor) - should not happen in normal flow
            actorLogger.Warning($"Streaming chunk received from unknown agent {agentSender.Path}, forwarding to parent");
            Context.Parent.Tell(chunk);
        }
    }

    /// <summary>
    /// Handles agent restoration requests from ConversationSupervisorActor.
    /// Resolves and caches the agent, making it immediately available for routing.
    /// </summary>
    /// <param name="req">Restoration request with agent intent</param>
    private async Task HandleRestoreAgentRequestAsync(Records.RestoreAgentRequest req)
    {
        IActorRef originalSender = Sender;

        actorLogger.Info($"Restoring agent for intent '{req.AgentIntent}'");

        IActorRef? agentRef = await GetOrCreateAgentForIntent(req.AgentIntent);

        if (agentRef != null)
        {
            actorLogger.Info($"Agent restored and cached: {agentRef.Path}");
        }
        else
        {
            actorLogger.Warning($"Could not restore agent for intent '{req.AgentIntent}' - no matching agent type");
        }

        originalSender.Tell(new Records.RestoreAgentResponse(req.AgentIntent, agentRef));
    }
}