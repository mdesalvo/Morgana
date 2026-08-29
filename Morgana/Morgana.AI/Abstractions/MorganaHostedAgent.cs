using System.Runtime.CompilerServices;
using System.Text.Json;
using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.AI.SessionStores;

namespace Morgana.AI.Abstractions;

/// <summary>
/// The <see cref="AIAgent"/> under which one Morgana intent is published over A2A: it owns no model
/// and no session, and carries an inbound request to the actor serving that intent.
/// </summary>
/// <remarks>
/// The seam between two ownership models — A2A hosting expects one long-lived agent per name, while
/// Morgana's agents are per-conversation actors — reconciled by the A2A <c>contextId</c>. Registered
/// once per intent as a singleton, so it holds no per-conversation state of its own.
/// </remarks>
public sealed class MorganaHostedAgent : AIAgent
{
    /// <summary>Intent this hosted agent publishes; fixed for its lifetime.</summary>
    private readonly string intent;

    /// <summary>Human-readable purpose, taken from the same card the well-known endpoint serves.</summary>
    private readonly string description;

    /// <summary>Maps the intent to its agent type, so the conversation's actor can be resolved or created.</summary>
    private readonly IAgentRegistryService agentRegistryService;

    /// <summary>
    /// Composes the note prefixed to every question, which is the one signal telling the answering
    /// agent that this turn's reader is a colleague and not the user.
    /// </summary>
    private readonly IPromptComposerService promptComposerService;

    /// <summary>
    /// Hands back the actor system, resolved on the turn that needs it rather than on construction.
    /// </summary>
    private readonly Func<ActorSystem> actorSystemResolver;

    /// <summary>How long to wait for the serving actor before reporting the agent unreachable.</summary>
    private readonly TimeSpan requestTimeout;

    /// <summary>Logger for inbound-request diagnostics.</summary>
    private readonly ILogger logger;

    /// <inheritdoc />
    public override string Name => intent;

    /// <inheritdoc />
    public override string Description => description;

    /// <summary>Publishes one intent, which must be handled by a registered Morgana agent.</summary>
    /// <param name="intent">Intent published under this name, and the agent's own name on its card.</param>
    /// <param name="description">Purpose of the agent, as advertised on its card.</param>
    /// <param name="agentRegistryService">Resolves the intent to the agent type serving it.</param>
    /// <param name="promptComposerService">Composes the note declaring that this turn serves a colleague.</param>
    /// <param name="actorSystemResolver">Hands back the actor system on the turn that needs it — see the field's own remarks for why it arrives as a delegate.</param>
    /// <param name="requestTimeout">Maximum wait for the serving actor's answer.</param>
    /// <param name="logger">Records requests that name no conversation, no agent, or that go unanswered.</param>
    public MorganaHostedAgent(
        string intent,
        string description,
        IAgentRegistryService agentRegistryService,
        IPromptComposerService promptComposerService,
        Func<ActorSystem> actorSystemResolver,
        TimeSpan requestTimeout,
        ILogger logger)
    {
        this.intent = intent;
        this.description = description;
        this.agentRegistryService = agentRegistryService;
        this.promptComposerService = promptComposerService;
        this.actorSystemResolver = actorSystemResolver;
        this.requestTimeout = requestTimeout;
        this.logger = logger;
    }

    /// <summary>
    /// Carries the request to the actor serving this intent in the conversation the session names,
    /// answering with the serialized <see cref="Records.PeerConsultationResponse"/> envelope.
    /// </summary>
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken = default)
    {
        // The conversation arrives as the session the store built from the A2A context id. Any other
        // session type means this agent was invoked outside the hosting pipeline it exists for.
        if (session is not MorganaHostedAgentSession hostedAgentSession)
        {
            logger.LogError("Hosted agent '{Intent}' was invoked with session type '{SessionType}', which carries no conversation", intent, session?.GetType().Name ?? "null");
            return BuildAgentResponseFromMessage($"The request for '{intent}' named no conversation and cannot be served.");
        }

        // One question out of however many parts the protocol delivered: A2A carries a message, not a
        // sentence, and the colleague is owed the whole of what was asked in a single turn — it has
        // no way to come back for the rest.
        string question = string.Join("\n", messages.Select(m => m.Text).Where(text => !string.IsNullOrWhiteSpace(text))).Trim();

        // Who is asking travels as metadata beside the message rather than inside it, which is what
        // keeps the question the caller's own words: an agent introducing itself in prose would be
        // spending the colleague's reading on its own name.
        string callerIntent = ReadCallerIntent(options);

        // Asked of the registry per request, never assumed from the fact that this endpoint answers.
        // Publication is decided once at startup, while the endpoint is open to anything that speaks
        // A2A — so a request naming an intent this installation no longer serves is an ordinary
        // request with a plain answer, not a fault to throw at a caller mid-turn.
        Type? agentType = agentRegistryService.ResolveAgentFromIntent(intent);
        if (agentType is null)
        {
            logger.LogError("Hosted agent '{Intent}' has no Morgana agent behind it", intent);
            return BuildAgentResponseFromMessage($"No agent answers for '{intent}'.");
        }

        try
        {
            // Resolve the actor system
            ActorSystem actorSystem = actorSystemResolver();

            // Deliberately the same resolution the router performs, so an agent reached over A2A is
            // the very same actor instance a user request would have been routed to: one session per
            // agent per conversation, whoever knocks.
            IActorRef agentActor = await actorSystem.GetOrCreateAgentAsync(agentType, intent, hostedAgentSession.ConversationId);

            // The note goes in front of the question rather than into the answering agent's prompt:
            // that prompt is composed once, while whether a turn serves a colleague changes turn by
            // turn, and it is the only thing telling the agent its reader is not the user.
            string declaredQuestion = await promptComposerService.ComposeConsultationRequestAsync(callerIntent) + question;

            // Ask, where the pipeline's own convention is Tell. That convention exists for streaming —
            // an actor pushing chunks to a channel as they come — and there is no channel here: a
            // colleague's answer is read whole, by a model, with a caller blocked on it. The timeout
            // is the pipeline's own, so a silent actor lands in the catch below as an answer instead
            // of hanging the user's turn.
            Records.PeerConsultationResponse response = await agentActor.Ask<Records.PeerConsultationResponse>(
                new Records.PeerConsultation(hostedAgentSession.ConversationId, callerIntent, declaredQuestion),
                requestTimeout,
                cancellationToken);

            // The envelope travels serialized inside an assistant message because that is the only
            // shape A2A carries, but it is DATA and not prose: what the asking agent receives is a
            // tool result to read and decide against, never something to relay to the user as it
            // stands.
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, SerializePeerConsultationResponse(response)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hosted agent '{Intent}' failed to serve a request from '{CallerIntent}' on conversation '{ConversationId}'", intent, callerIntent, hostedAgentSession.ConversationId);
            return BuildAgentResponseFromMessage($"The agent for '{intent}' did not answer in time. Proceed without it.");
        }
    }

    /// <summary>Streaming form of <see cref="RunCoreAsync"/>, emitting the answer as a single update.</summary>
    /// <remarks>
    /// Nobody watches a consultation, and the published card declares no streaming: this exists
    /// because <see cref="AIAgent"/> requires it.
    /// </remarks>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        AgentResponse response = await RunCoreAsync(messages, session, options, cancellationToken);

        yield return new AgentResponseUpdate(ChatRole.Assistant, response.Text);
    }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            $"Sessions of the hosted agent '{intent}' are created by {nameof(MorganaHostedAgentSessionStore)} from the A2A context id, never by the agent itself.");

    /// <inheritdoc />
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(JsonSerializer.SerializeToElement(session as MorganaHostedAgentSession, jsonSerializerOptions));

    /// <inheritdoc />
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<AgentSession>(
            serializedState.Deserialize<MorganaHostedAgentSession>(jsonSerializerOptions)
             ?? throw new InvalidOperationException($"The serialized session handed to hosted agent '{intent}' names no conversation."));

    /// <summary>
    /// Reads the asking agent's intent from the run options the A2A layer rebuilt from the request's
    /// metadata, falling back to a neutral label when the caller is not a Morgana agent.
    /// </summary>
    /// <param name="options">Run options assembled by the A2A hosting layer for this request.</param>
    private static string ReadCallerIntent(AgentRunOptions? options)
        => options?.AdditionalProperties?.TryGetValue(Constants.MessageProperties.CallerIntent, out object? caller) == true && caller is not null
            ? caller.ToString() ?? "unknown"
            : "unknown";

    /// <summary>
    /// Wraps a framework-authored message in the same envelope a real answer travels in, so the
    /// asking model always parses one shape whatever happened.
    /// </summary>
    private static AgentResponse BuildAgentResponseFromMessage(string message)
        => new AgentResponse(new ChatMessage(ChatRole.Assistant,
            SerializePeerConsultationResponse(new Records.PeerConsultationResponse(message, false))));

    /// <summary>Renders the envelope the asking agent's model receives as the tool result.</summary>
    private static string SerializePeerConsultationResponse(Records.PeerConsultationResponse peerConsultationResponse)
        => JsonSerializer.Serialize(peerConsultationResponse, Records.DefaultJsonSerializerOptions);
}