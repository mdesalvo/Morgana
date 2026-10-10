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
        // The registry is what turns an intent into the agent type that serves it.
        this.agentResolverService = agentResolverService;

        // A classified request goes to its agent and the agent's final answer comes back through Tell,
        // since the answer is not one reply but a stream followed by a closing response.
        ReceiveAsync<Records.AgentRequest>(RouteToAgentAsync);
        Receive<Records.AgentResponse>(HandleAgentResponseDirect);

        // The agent has no reference to the supervisor, so its chunks reach the user through this relay.
        Receive<Records.AgentStreamChunk>(HandleAgentStreamChunk);

        // Carry an agent's sign of life to the supervisor, which is the only party timing it.
        Receive<Records.AgentStillWorking>(stillWorking => ForwardToSupervisor(stillWorking, "sign of life"));

        // A resumed conversation asks for its active agent back before the user's next message arrives.
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
        // An agent already routed to in this conversation is the same actor for every later turn.
        if (agents.TryGetValue(intent, out IActorRef? cachedAgent))
        {
            actorLogger.Info($"Using cached agent for intent '{intent}': {cachedAgent.Path}");
            return cachedAgent;
        }

        // An intent that no [HandlesIntent] class serves has no agent type: the caller answers the user.
        Type? agentType = agentResolverService.ResolveAgentFromIntent(intent);
        if (agentType == null)
        {
            actorLogger.Warning($"No agent type found for intent '{intent}'");
            return null;
        }

        // After a resume the actor may already exist under the conversation: it is reused, which keeps one session per agent.
        IActorRef agent = await Context.System.GetOrCreateAgentAsync(agentType, intent, conversationId);

        // Cached so that the next turn does not go through the registry again.
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
        // The sender is captured before the first await: it is the supervisor waiting for the agent's answer.
        IActorRef originalSender = Sender;

        // The supervisor is always the originator of AgentRequest — cache it on first contact
        // so late stream chunks (see HandleAgentStreamChunk fallback) can still be routed to it.
        supervisorRef ??= originalSender;

        // The agent that serves the classified intent, started on its first request.
        IActorRef? selectedAgent = await GetOrCreateAgentForIntent(req.Classification!.Intent);

        // An intent without an agent is answered in Morgana's words and the turn ends there.
        if (selectedAgent == null)
        {
            Records.Prompt classifierPrompt = await promptResolverService.ResolveAsync(Constants.Prompts.Classifier);
            string unrecognizedIntentError = classifierPrompt.GetMessage(Constants.Messages.UnrecognizedIntent);
            originalSender.Tell(new Records.AgentResponse(unrecognizedIntentError, true));
            return;
        }

        actorLogger.Info($"Routing intent '{req.Classification.Intent}' to agent {selectedAgent.Path}");

        // The agent's chunks and answer find their way back to the supervisor through this entry.
        streamingContexts[selectedAgent] = originalSender;

        // The agent answers in several messages (chunks and a closing response), so the request is told and not asked.
        selectedAgent.Tell(req);
    }

    /// <summary>
    /// Handles an agent's final response, once its own streaming (if any) has finished and
    /// forwards it to the supervisor wrapped with the agent reference it came from.
    /// </summary>
    /// <param name="response">Agent response from specialized agent</param>
    private void HandleAgentResponseDirect(Records.AgentResponse response)
    {
        // The sender identifies which routed request this response closes.
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
                response.RichCard,
                response.RecordedTimestamp));
        }
        else
        {
            // A response from an agent that no request was routed to has nobody waiting for it.
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
        => ForwardToSupervisor(chunk, "streaming chunk");

    /// <summary>
    /// Carries what an agent emits mid-turn to the supervisor waiting on it.
    /// </summary>
    /// <remarks>
    /// Both a partial response and a bare sign of life travel this way and for the same reason: the
    /// supervisor is the party timing the turn and the agent has no reference to it.
    /// </remarks>
    /// <param name="message">What the agent emitted.</param>
    /// <param name="description">What it was, for the diagnostics when there is nowhere to put it.</param>
    private void ForwardToSupervisor(object message, string description)
    {
        // The sender identifies which routed request this message belongs to.
        IActorRef agentSender = Sender;

        if (streamingContexts.TryGetValue(agentSender, out IActorRef? originalSender))
        {
            // The turn is in flight: the supervisor that routed it receives the message as it is.
            originalSender.Tell(message);
        }
        else if (supervisorRef is not null)
        {
            // Fallback: streamingContexts has already been cleaned up (e.g. late chunk after
            // AgentResponse). Forward to the cached supervisor ref — Context.Parent would
            // resolve to /user (router is flat under the guardian) and land in dead letters.
            actorLogger.Warning($"Late {description} from {agentSender.Path}, forwarding to supervisor");
            supervisorRef.Tell(message);
        }
        else
        {
            // No AgentRequest has been routed yet, so we have no supervisor ref to fall back to.
            // Dropping is correct: the message has no legitimate destination at this point.
            actorLogger.Warning($"{description} from {agentSender.Path} before any AgentRequest; dropping");
        }
    }

    /// <summary>
    /// Handles agent restoration requests from ConversationSupervisorActor.
    /// Resolves and caches the agent, making it immediately available for routing.
    /// </summary>
    /// <param name="req">Restoration request with agent intent</param>
    private async Task HandleRestoreAgentRequestAsync(Records.RestoreAgentRequest req)
    {
        // The supervisor waits for the restored reference: the sender is captured before the first await.
        IActorRef originalSender = Sender;

        actorLogger.Info($"Restoring agent for intent '{req.AgentIntent}'");

        // Get an instance for the agent serving the current intent:
        // this is handled by Akka.NET, which will rehydrate it if existing
        IActorRef? agentRef = await GetOrCreateAgentForIntent(req.AgentIntent);

        // A missing type is reported to the supervisor as a null reference, so it returns to Morgana.
        if (agentRef != null)
        {
            actorLogger.Info($"Agent restored and cached: {agentRef.Path}");
        }
        else
        {
            actorLogger.Warning($"Could not restore agent for intent '{req.AgentIntent}' - no matching agent type");
        }

        // The reference travels with the intent it was asked for, so the supervisor can match the answer to its request.
        originalSender.Tell(new Records.RestoreAgentResponse(req.AgentIntent, agentRef));
    }
}