using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Actors;

/// <summary>
/// Intent-to-agent routing actor that directs requests to specialized agents based on intent classification.
/// Maintains a registry of intent-to-agent mappings with lazy agent creation.
/// </summary>
public class RouterActor : MorganaActor
{
    /// <summary>
    /// Dictionary mapping intent names to their corresponding agent actor references.
    /// Populated lazily on first use of each agent. Case-insensitive to match
    /// <see cref="IAgentRegistryService.ResolveAgentFromIntent"/>, whose own registry is keyed the
    /// same way — a classifier response that varies in case from the configured intent must resolve
    /// to the same cached agent, not a duplicate cache miss.
    /// </summary>
    private readonly Dictionary<string, IActorRef> agents = new(StringComparer.OrdinalIgnoreCase);

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
    /// Reference to the <see cref="ConversationSupervisorActor"/>, captured from the first
    /// <see cref="Records.AgentRequest"/> (which the supervisor always originates).
    /// Used as the fallback destination for late stream chunks whose <c>streamingContexts</c>
    /// entry has already been cleaned up — routing them to <c>Context.Parent</c> would
    /// hit <c>/user</c> (the router is created flat under the guardian, not as a child
    /// of the supervisor) and land in dead letters.
    /// </summary>
    private IActorRef? supervisorRef;

    /// <summary>
    /// Initializes a new instance of the RouterActor.
    /// Does NOT pre-create agents - they are created on-demand when first needed.
    /// </summary>
    /// <param name="conversationId">Unique identifier for this conversation</param>
    /// <param name="llmService">LLM service for AI completions</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="agentResolverService">Service for agent discovery and resolution</param>
    /// <param name="configuration">Morgana configuration (layered by ASP.NET)</param>
    public RouterActor(
        string conversationId,
        ILLMService llmService,
        IPromptResolverService promptResolverService,
        IAgentRegistryService agentResolverService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        this.agentResolverService = agentResolverService;

        // Route classified requests to specialized agents based on intent:
        // - Validates classification exists and intent is recognized
        // - Creates agent on-demand if not yet created
        // - Forwards request to appropriate agent
        // - Receives both streaming chunks and final response via Tell
        // - Returns error messages for missing/unrecognized intents
        ReceiveAsync<Records.AgentRequest>(RouteToAgentAsync);
        Receive<Records.AgentResponse>(HandleAgentResponseDirect);

        // Forward streaming chunks from agents to supervisor
        Receive<Records.AgentStreamChunk>(HandleAgentStreamChunk);

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
        IActorRef agent = await Context.System.GetOrCreateAgentAsync(agentType, intent, conversationId);

        // Cache for future requests
        agents[intent] = agent;

        actorLogger.Info($"Agent created/resolved for intent '{intent}': {agent.Path}");
        return agent;
    }

    /// <summary>
    /// Routes an agent request to the appropriate specialized agent, creating it on-demand
    /// if this intent hasn't been routed to yet.
    /// </summary>
    /// <param name="req">Agent request containing classification and message data</param>
    private async Task RouteToAgentAsync(Records.AgentRequest req)
    {
        IActorRef originalSender = Sender;

        // The supervisor is always the originator of AgentRequest — cache it on first contact
        // so late stream chunks (see HandleAgentStreamChunk fallback) can still be routed to it.
        supervisorRef ??= originalSender;

        // Get or create agent for this intent
        IActorRef? selectedAgent = await GetOrCreateAgentForIntent(req.Classification!.Intent);

        // Validate that an agent exists for this intent
        if (selectedAgent == null)
        {
            // No [HandlesIntent] agent is registered for this intent
            Records.Prompt classifierPrompt = await promptResolverService.ResolveAsync(Constants.Prompts.Classifier);
            string unrecognizedIntentError = classifierPrompt.GetAdditionalProperty<string>("UnrecognizedIntentError");
            originalSender.Tell(new Records.AgentResponse(unrecognizedIntentError, true));
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
    /// Handles an agent's final response, once its own streaming (if any) has finished, and
    /// forwards it to the supervisor wrapped with the agent reference it came from.
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

            // The exchange with this agent is over — its slot in the map is freed for the next
            // request this same agent instance might be routed to in a later turn.
            streamingContexts.Remove(agentSender);

            // ActiveAgentResponse is what carries agentSender onward: it's how the supervisor
            // learns which agent actor to keep as activeAgent for this conversation's follow-ups.
            originalSender.Tell(new Records.ActiveAgentResponse(
                response.Response,
                response.IsCompleted,
                agentSender,
                response.QuickReplies,
                response.RichCard));
        }
        else
        {
            actorLogger.Warning($"Received response from unknown agent {agentSender.Path}");
        }
    }

    /// <summary>
    /// Handles streaming chunks from agents and forwards them to the original sender (supervisor).
    /// Uses streamingContexts map to find the correct destination.
    /// Enables real-time progressive response rendering.
    /// </summary>
    /// <param name="chunk">Streaming chunk from agent</param>
    private void HandleAgentStreamChunk(Records.AgentStreamChunk chunk)
    {
        IActorRef agentSender = Sender;

        if (streamingContexts.TryGetValue(agentSender, out IActorRef? originalSender))
        {
            // Forward chunk to original sender (supervisor)
            originalSender.Tell(chunk);
        }
        else if (supervisorRef is not null)
        {
            // Fallback: streamingContexts has already been cleaned up (e.g. late chunk after
            // AgentResponse). Forward to the cached supervisor ref — Context.Parent would
            // resolve to /user (router is flat under the guardian) and land in dead letters.
            actorLogger.Warning($"Late streaming chunk from {agentSender.Path}, forwarding to supervisor");
            supervisorRef.Tell(chunk);
        }
        else
        {
            // No AgentRequest has been routed yet, so we have no supervisor ref to fall back to.
            // Dropping is correct: the message has no legitimate destination at this point.
            actorLogger.Warning($"Streaming chunk from {agentSender.Path} before any AgentRequest; dropping");
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

        // Get an instance for the agent serving the current intent:
        // this is handled by Akka.NET, which will rehydrate it if existing
        IActorRef? agentRef = await GetOrCreateAgentForIntent(req.AgentIntent);

        if (agentRef != null)
        {
            actorLogger.Info($"Agent restored and cached: {agentRef.Path}");
        }
        else
        {
            actorLogger.Warning($"Could not restore agent for intent '{req.AgentIntent}' - no matching agent type");
        }

        // Answer the supervisor with the rehydrated agent reference
        originalSender.Tell(new Records.RestoreAgentResponse(req.AgentIntent, agentRef));
    }
}