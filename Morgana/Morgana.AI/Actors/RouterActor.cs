using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Abstractions;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Actors;

/// <summary>
/// The conversation's router: it brings up the agent of a classified intent and relays what the agent sends back.
/// An agent is brought up only on its intent's first request, so a conversation holds only the agents it has used.
/// </summary>
public class RouterActor : MorganaActor
{
    /// <summary>
    /// The agents already brought up for this conversation, by intent.
    /// Case-insensitive like the registry, so a classifier answer that varies in case reaches the same agent.
    /// </summary>
    private readonly Dictionary<string, IActorRef> agents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Who waits on each agent's running turn, so its chunks and its answer reach that party.</summary>
    private readonly Dictionary<IActorRef, IActorRef> streamingContexts = [];

    /// <summary>Resolves the agent type that handles an intent.</summary>
    private readonly IAgentRegistryService agentResolverService;

    /// <summary>Writes the presentation that a request no agent claims is answered with.</summary>
    private readonly IPresenterService presenterService;

    /// <summary>The configured intents, which the presentation offers as buttons.</summary>
    private readonly IAgentConfigurationService agentConfigService;

    /// <summary>
    /// The supervisor, taken from the first request it routes, where a late chunk of an agent is delivered.
    /// The router lives flat under the guardian, so its parent is not the supervisor and would send the chunk to dead letters.
    /// </summary>
    private IActorRef? supervisorRef;

    /// <summary>Binds the router to its conversation and to the services that find agents and answer for none.</summary>
    public RouterActor(
        string conversationId,
        ILanguageModelService llmService,
        IPromptResolverService promptResolverService,
        IAgentRegistryService agentResolverService,
        IPresenterService presenterService,
        IAgentConfigurationService agentConfigService,
        IConfiguration configuration) : base(conversationId, llmService, promptResolverService, configuration)
    {
        this.agentResolverService = agentResolverService;
        this.presenterService = presenterService;
        this.agentConfigService = agentConfigService;

        // A classified request reaches its agent; Morgana answers it herself when no agent handles the intent.
        ReceiveAsync<Records.AgentRequest>(RouteToAgentAsync);

        // An agent's final answer goes back to the supervisor, naming the agent that gave it.
        Receive<Records.AgentResponse>(HandleAgentResponseDirect);

        // A streamed piece of an answer goes on to the supervisor.
        Receive<Records.AgentStreamChunk>(HandleAgentStreamChunk);

        // Carry an agent's sign of life to the supervisor, which is the only party timing it.
        Receive<Records.AgentStillWorking>(stillWorking => ForwardToSupervisor(stillWorking, "sign of life"));

        // The supervisor asks back the agent its conversation was left talking to.
        ReceiveAsync<Records.RestoreAgentRequest>(HandleRestoreAgentRequestAsync);
    }

    /// <summary>
    /// Stops every agent of the conversation as the router stops with its supervisor.
    /// An agent can also be brought up by the A2A door for a colleague's question, so each one is reached by its name rather than from the router's own list.
    /// </summary>
    protected override void PostStop()
    {
        // Every registered intent names at most one agent of this conversation; a name with no actor behind it reaches nobody.
        foreach (string intent in agentResolverService.GetAllIntents())
            Context.System.ActorSelection($"/user/{intent}-{conversationId}").Tell(PoisonPill.Instance);

        base.PostStop();
    }

    /// <summary>Returns the conversation's agent for the intent, bringing it up on first use; null when no agent handles the intent.</summary>
    /// <param name="intent">The intent name, as the classifier or the record gives it.</param>
    private async Task<IActorRef?> GetOrCreateAgentForIntent(string intent)
    {
        // An agent already brought up serves every later request of its intent.
        if (agents.TryGetValue(intent, out IActorRef? cachedAgent))
        {
            actorLogger.Info($"Using cached agent for intent '{intent}': {cachedAgent.Path}");
            return cachedAgent;
        }

        // The registry knows which agent type handles the intent: Other and an unknown name have none.
        Type? agentType = agentResolverService.ResolveAgentFromIntent(intent);
        if (agentType == null)
        {
            actorLogger.Warning($"No agent type found for intent '{intent}'");
            return null;
        }

        // An agent of this conversation still alive under its name is reused, otherwise a new one is created.
        IActorRef agent = await Context.System.GetOrCreateAgentAsync(agentType, intent, conversationId);

        // The agent serves every later request of its intent.
        agents[intent] = agent;

        actorLogger.Info($"Agent created/resolved for intent '{intent}': {agent.Path}");
        return agent;
    }

    /// <summary>
    /// Hands the request to its intent's agent; an intent with no agent is answered with the presentation
    /// or, after a classification failure, with the classification-error message.
    /// </summary>
    /// <param name="req">The request, carrying the classification that names the intent.</param>
    private async Task RouteToAgentAsync(Records.AgentRequest req)
    {
        // The supervisor of the conversation, which waits for the agent's answer.
        IActorRef originalSender = Sender;

        // The supervisor is always the originator of AgentRequest — cache it on first contact
        // so late stream chunks (see HandleAgentStreamChunk fallback) can still be routed to it.
        supervisorRef ??= originalSender;

        // The supervisor always classifies before routing, so the classification is there.
        IActorRef? selectedAgent = await GetOrCreateAgentForIntent(req.Classification!.Intent);

        // No [HandlesIntent] agent is registered for this intent: Morgana answers it herself
        if (selectedAgent == null)
        {
            // A classification marked as a failure must not push a menu at a user whose request may have been perfectly legitimate.
            if (req.Classification.Metadata.ContainsKey(Constants.ClassificationMetadata.Error))
            {
                // The classification-error wording comes from the classifier's prompt, in Morgana's own voice.
                Records.Prompt classifierPrompt = await promptResolverService.ResolveAsync(Constants.Prompts.Classifier);

                // The fallback was technical: the classification-error wording alone answers it, with no menu.
                string classificationError = classifierPrompt.GetAdditionalProperty<string>(Constants.PromptProperties.ClassificationError);
                originalSender.Tell(new Records.AgentResponse(classificationError, true));
            }
            // A genuine complement is a request that no agent claims: it is answered by showing what the domain offers.
            else
            {
                // The presentation offers a button for every intent that has one to show.
                List<Records.IntentDefinition> configuredIntents = await agentConfigService.GetIntentsAsync();
                Records.IntentCollection intentCollection = new Records.IntentCollection(configuredIntents);

                // The presentation is cached per channel: showing it again costs no model call.
                Records.PresentationResult presentation = await presenterService.GenerateAsync(intentCollection.GetDisplayableIntents(), conversationId);

                // The reintroduction alone answers it: it holds no greeting since the user is already in the
                // conversation and closes on every intent's button.
                originalSender.Tell(new Records.AgentResponse(presentation.Reintroduction, true, presentation.QuickReplies));
            }
            return;
        }

        actorLogger.Info($"Routing intent '{req.Classification.Intent}' to agent {selectedAgent.Path}");

        // The agent's chunks and its answer go back to whoever is waiting on this turn.
        streamingContexts[selectedAgent] = originalSender;

        // The agent answers by message, so its chunks can arrive before its answer.
        selectedAgent.Tell(req);
    }

    /// <summary>Hands an agent's final answer to the supervisor, naming the agent that gave it.</summary>
    /// <param name="response">The agent's final answer.</param>
    private void HandleAgentResponseDirect(Records.AgentResponse response)
    {
        // The agent that answered is the one whose turn ends here.
        IActorRef agentSender = Sender;

        // An agent with a turn in flight has somebody waiting on its answer.
        if (streamingContexts.TryGetValue(agentSender, out IActorRef? originalSender))
        {
            actorLogger.Info($"Received response from agent {agentSender.Path}, " +
                             $"completed: {response.IsCompleted}, " +
                             $"#quickReplies: {response.QuickReplies?.Count ?? 0}");

            // The exchange with this agent is over, so its next turn registers afresh.
            streamingContexts.Remove(agentSender);

            // The agent's reference travels with the answer: it is how the supervisor learns which agent to keep for the follow-ups.
            originalSender.Tell(new Records.ActiveAgentResponse(
                response.Response,
                response.IsCompleted,
                agentSender,
                response.QuickReplies,
                response.RichCard,
                response.RecordedTimestamp));
        }
        // An answer from an agent with no turn in flight has nobody waiting on it.
        else
        {
            actorLogger.Warning($"Received response from unknown agent {agentSender.Path}");
        }
    }

    /// <summary>Hands a streamed piece of an agent's answer to the supervisor.</summary>
    /// <param name="chunk">The piece of the answer.</param>
    private void HandleAgentStreamChunk(Records.AgentStreamChunk chunk)
        => ForwardToSupervisor(chunk, "streaming chunk");

    /// <summary>
    /// Carries what an agent emits mid-turn to the supervisor waiting on it.
    /// A partial response and a bare sign of life travel this way for the same reason: the supervisor
    /// is the party timing the turn and the agent has no reference to it.
    /// </summary>
    /// <param name="message">What the agent emitted.</param>
    /// <param name="description">What it was, for the diagnostics when there is nowhere to put it.</param>
    private void ForwardToSupervisor(object message, string description)
    {
        // The agent that emitted the message names the turn it belongs to.
        IActorRef agentSender = Sender;

        // The turn is in flight: the message reaches whoever is waiting on it.
        if (streamingContexts.TryGetValue(agentSender, out IActorRef? originalSender))
        {
            originalSender.Tell(message);
        }
        // The turn has already been answered, so the late message goes to the supervisor taken from the first request.
        else if (supervisorRef is not null)
        {
            actorLogger.Warning($"Late {description} from {agentSender.Path}, forwarding to supervisor");
            supervisorRef.Tell(message);
        }
        // No request has been routed yet, so the message has no legitimate destination and is dropped.
        else
        {
            actorLogger.Warning($"{description} from {agentSender.Path} before any AgentRequest; dropping");
        }
    }

    /// <summary>Brings up the agent that the conversation's record names as active and answers the supervisor with it.</summary>
    /// <param name="req">The request naming the agent's intent.</param>
    private async Task HandleRestoreAgentRequestAsync(Records.RestoreAgentRequest req)
    {
        // The supervisor of the conversation, which waits for the restored agent.
        IActorRef originalSender = Sender;

        actorLogger.Info($"Restoring agent for intent '{req.AgentIntent}'");

        // The agent is brought up as on a routed request, so the next message finds it ready.
        IActorRef? agentRef = await GetOrCreateAgentForIntent(req.AgentIntent);

        if (agentRef != null)
        {
            actorLogger.Info($"Agent restored and cached: {agentRef.Path}");
        }
        else
        {
            actorLogger.Warning($"Could not restore agent for intent '{req.AgentIntent}' - no matching agent type");
        }

        // The supervisor receives the agent; it receives none when no agent handles that intent.
        originalSender.Tell(new Records.RestoreAgentResponse(req.AgentIntent, agentRef));
    }
}