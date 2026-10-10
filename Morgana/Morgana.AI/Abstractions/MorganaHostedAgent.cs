using System.Runtime.CompilerServices;
using System.Text.Json;
using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions;

/// <summary>
/// The <see cref="AIAgent"/> under which one Morgana intent is published over A2A: it owns no model
/// and no session and carries an inbound request to the actor serving that intent.
/// </summary>
/// <remarks>
/// The seam between two ownership models — A2A hosting expects one long-lived agent per name, while
/// Morgana's agents are per-conversation actors — reconciled by the A2A <c>contextId</c>. Registered
/// once per intent as a singleton, so it holds no per-conversation state of its own.
/// </remarks>
public sealed class MorganaHostedAgent : AIAgent
{
    /// <summary>
    /// Longest conversation name an inbound request may be served on.
    /// </summary>
    /// <remarks>
    /// A conversation of this installation is an identifier of a few dozen characters, so the
    /// ceiling costs an honest caller nothing. It is there because the name reaches a filesystem,
    /// whose own limit on a path component would otherwise be met mid-turn by a caller free to
    /// write whatever it likes.
    /// </remarks>
    private const int MaximumConversationNameLength = 128;

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

    /// <summary>
    /// The conversation's ledger, asked what serving one consultation cost so the answer can carry
    /// it back. Every dust question — reading, delta, clamping — is its own, never computed here.
    /// </summary>
    private readonly IDustLimitService dustLimitService;

    /// <summary>
    /// How many conversations the asking system may still open. Behind this door the caller names
    /// the conversation, so the conversation's own budget bounds the exchange but not the caller.
    /// </summary>
    private readonly IPeerAdmissionService peerAdmissionService;

    /// <summary>
    /// Owner of the conversation's storage, asked to open it before the turn runs. A partner's
    /// exchange has no database of its own until one is made. Until then nothing that turn
    /// spends is recorded anywhere.
    /// </summary>
    private readonly IConversationPersistenceService persistenceService;

    /// <summary>Logger for inbound-request diagnostics.</summary>
    private readonly ILogger logger;

    /// <inheritdoc />
    public override string Name => intent;

    /// <inheritdoc />
    public override string Description => description;

    /// <summary>Publishes one intent, which must be handled by a registered Morgana agent.</summary>
    /// <param name="intent">Intent published under this name and the agent's own name on its card.</param>
    /// <param name="description">Purpose of the agent, as advertised on its card.</param>
    /// <param name="agentRegistryService">Resolves the intent to the agent type serving it.</param>
    /// <param name="promptComposerService">Composes the note declaring that this turn serves a colleague.</param>
    /// <param name="actorSystemResolver">Hands back the actor system on the turn that needs it — see the field's own remarks for why it arrives as a delegate.</param>
    /// <param name="requestTimeout">Maximum wait for the serving actor's answer.</param>
    /// <param name="dustLimitService">Ledger consulted for what the served turn cost.</param>
    /// <param name="peerAdmissionService">Weighs a system opening a conversation it has not opened before.</param>
    /// <param name="persistenceService">Owner of the conversation's storage, opened before the turn runs.</param>
    /// <param name="logger">Records requests that name no conversation, no agent, or that go unanswered.</param>
    public MorganaHostedAgent(
        string intent,
        string description,
        IAgentRegistryService agentRegistryService,
        IPromptComposerService promptComposerService,
        Func<ActorSystem> actorSystemResolver,
        TimeSpan requestTimeout,
        IDustLimitService dustLimitService,
        IPeerAdmissionService peerAdmissionService,
        IConversationPersistenceService persistenceService,
        ILogger logger)
    {
        // The agent is a singleton per intent: everything it needs is fixed at registration and nothing is per conversation.
        this.intent = intent;
        this.description = description;
        this.agentRegistryService = agentRegistryService;
        this.promptComposerService = promptComposerService;
        this.actorSystemResolver = actorSystemResolver;
        this.requestTimeout = requestTimeout;
        this.dustLimitService = dustLimitService;
        this.peerAdmissionService = peerAdmissionService;
        this.persistenceService = persistenceService;
        this.logger = logger;
    }

    /// <summary>
    /// Carries the request to the actor serving this intent in the conversation the session names,
    /// answering with the serialized <see cref="Records.PeerConsultationResponse"/> envelope.
    /// </summary>
    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The conversation arrives as the session the store built from the A2A context id. Any other
        // session type means this agent was invoked outside the hosting pipeline it exists for.
        if (session is not MorganaHostedAgentSession hostedAgentSession)
        {
            logger.LogError("Hosted agent '{Intent}' was invoked with session type '{SessionType}', which carries no conversation", intent, session?.GetType().Name ?? "null");
            return BuildAgentResponseFromMessage($"The request for '{intent}' named no conversation and cannot be served.");
        }

        // The name is the caller's context id namespaced under the issuer that was proven, which this
        // installation raises an actor and opens a database under. Neither takes every string: a
        // separator or a space names an actor Akka refuses, an unbounded one a path no filesystem
        // opens — both after the exchange was admitted, mid-turn. Refused here in prose instead.
        if (hostedAgentSession.ConversationId.Length > MaximumConversationNameLength
            || !ActorPath.IsValidPathElement(hostedAgentSession.ConversationId))
        {
            logger.LogWarning(
                "Hosted agent '{Intent}' refused a request from '{CallerIssuer}': the context id names no conversation this installation can serve on",
                intent, hostedAgentSession.CallerIssuer ?? "an undeclared system");

            return BuildAgentResponseFromMessage(
                $"The request for '{intent}' named a conversation this installation cannot serve on: a context id must carry no path "
                + $"separator, space or control character and must resolve to at most {MaximumConversationNameLength} characters.");
        }

        // One question out of however many parts the protocol delivered: A2A carries a message, not a
        // sentence and the colleague is owed the whole of what was asked in a single turn — it has
        // no way to come back for the rest.
        string question = string.Join("\n", messages.Select(m => m.Text).Where(text => !string.IsNullOrWhiteSpace(text))).Trim();

        // Who is asking travels as metadata beside the message rather than inside it, which is what
        // keeps the question the caller's own words: an agent introducing itself in prose would be
        // spending the colleague's reading on its own name. Only an agent of this installation
        // declares itself and the endpoint answers anything that speaks A2A — so an unnamed caller
        // is ordinary and what to call it is the composer's word to choose, not this method's.
        string? callerIntent = options?.AdditionalProperties?.GetValueOrDefault(Constants.MessageProperties.CallerIntent)?.ToString();

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
            // An exchange this installation has never seen. A caller admitted here reaches an agent
            // directly, with none of the rate limit a channel's own path goes through. It also writes
            // the name of the conversation it is served on — so a partner free to keep writing new
            // ones would draw a fresh budget with every one of them. What a turned-away partner reads
            // is written on its own entry, so the refusal speaks in this deployment's voice.
            if (!persistenceService.ConversationExists(hostedAgentSession.ConversationId)
                && hostedAgentSession.CallerIssuer is { } openingIssuer
                && await peerAdmissionService.TryAdmitNewConversationAsync(openingIssuer) is { IsAdmitted: false } refusal)
            {
                return BuildAgentResponseFromMessage(
                    refusal.RefusalMessage ?? await ComposeFallbackAsync(Constants.ToolInjections.PeerAtCapacity));
            }

            // Opened before the turn, because a partner's exchange has none until now: the ledger is
            // where everything this turn spends is recorded. What is not recorded can be neither
            // reported back to the caller nor held against the budget below.
            await persistenceService.EnsureDatabaseInitializedAsync(hostedAgentSession.ConversationId);

            // A conversation that has already spent its budget buys nothing more, whoever is asking.
            // For a colleague of this installation this is the very budget the user's own turns are
            // held to — reached over A2A instead of through the pipeline, held to it just the same.
            if (await dustLimitService.IsOverBudgetAsync(hostedAgentSession.ConversationId))
            {
                logger.LogWarning(
                    "Hosted agent '{Intent}' refused a request from '{CallerIntent}': conversation '{ConversationId}' is over budget",
                    intent, callerIntent, hostedAgentSession.ConversationId);

                return BuildAgentResponseFromMessage(await ComposeFallbackAsync(Constants.ToolInjections.PeerOutOfBudget));
            }

            // The actor system exists only after the host has started, which is later than this singleton's construction.
            ActorSystem actorSystem = actorSystemResolver();

            // Deliberately the same resolution the router performs, so an agent reached over A2A is
            // the very same actor instance a user request would have been routed to: one session per
            // agent per conversation, whoever knocks.
            IActorRef agentActor = await actorSystem.GetOrCreateAgentAsync(agentType, intent, hostedAgentSession.ConversationId);

            // The note and the fence around the question are one composition: what a colleague wrote is
            // the only text on this turn authored outside this installation. Where it begins and
            // ends is stated by the same layer that says what it may not claim.
            string declaredQuestion = await promptComposerService.ComposeConsultationRequestAsync(callerIntent, question);

            // Taken before the turn so the ledger can be asked afterwards what happened in between.
            // The reading is of the whole conversation rather than of this exchange, which has no
            // ledger of its own — the difference is what isolates the answer's own cost.
            double dustBaseline = await dustLimitService.GetConsumedAsync(hostedAgentSession.ConversationId);

            // Ask, where the pipeline's own convention is Tell. That convention exists for streaming,
            // an actor pushing chunks to a channel as they come. There is no channel here: a
            // colleague's answer is read whole, by a model, with a caller blocked on it. The timeout
            // is the pipeline's own, so a silent actor lands in the catch below as an answer instead
            // of hanging the user's turn.
            Records.PeerConsultationResponse peerConsultationResponse = await agentActor.Ask<Records.PeerConsultationResponse>(
                new Records.PeerConsultation(hostedAgentSession.ConversationId, callerIntent, declaredQuestion),
                requestTimeout,
                cancellationToken);

            // What the answer cost travels back only to a caller that declared itself an agent of a
            // Morgana: anything else that speaks A2A has no ledger to charge it to and would carry a
            // number it cannot read. Unreported, the spend simply stays ours — it is on our own books
            // either way and this line only decides whether the asker gets to see it.
            double dustSpent = await dustLimitService.GetConsumedSinceAsync(hostedAgentSession.ConversationId, dustBaseline);

            if (callerIntent is not null)
                peerConsultationResponse = peerConsultationResponse with { DustConsumed = dustSpent };

            // The envelope travels serialized inside an assistant message because that is the only
            // shape A2A carries, but it is DATA and not prose: what the asking agent receives is a
            // tool result to read and decide against, never something to relay to the user as it stands.
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, SerializePeerConsultationResponse(peerConsultationResponse)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hosted agent '{Intent}' failed to serve a request from '{CallerIntent}' on conversation '{ConversationId}'", intent, callerIntent, hostedAgentSession.ConversationId);

            // Only an agent that went silent is reported as one. Anything else — an answer that would
            // not serialize, an actor that could not be reached — told the asking model to expect a
            // slow colleague when what it had was a broken one, which is a different thing to decide
            // against. Either way the instruction is the same: this answer is not coming.
            return BuildAgentResponseFromMessage(await ComposeFallbackAsync(ex is AskTimeoutException or OperationCanceledException
                ? Constants.ToolInjections.PeerTimedOut
                : Constants.ToolInjections.PeerFailed));
        }
    }

    /// <summary>
    /// The answer that a caller's model reads when this agent does not serve its request, worded in
    /// morgana.json and naming the agent that did not answer.
    /// </summary>
    private Task<string> ComposeFallbackAsync(string toolInjectionName)
        => promptComposerService.ComposeToolInjectionAsync(
            toolInjectionName, new Dictionary<string, string> { [Constants.Placeholders.AgentIntent] = intent });

    /// <summary>Streaming form of <see cref="RunCoreAsync"/>, emitting the answer as a single update.</summary>
    /// <remarks>
    /// Nobody watches a consultation and the published card declares no streaming: this exists
    /// because <see cref="AIAgent"/> requires it.
    /// </remarks>
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The whole consultation runs here, awaited and only then is emitted as one update: asking
        // for a stream is not a second path into a colleague, so the Ask, its timeout and the
        // serialized envelope hold identically whichever form the caller picked.
        AgentResponse response = await RunCoreAsync(messages, session, options, cancellationToken);

        // The envelope is the text, so a single update carries it whole — split across updates it
        // would reach the caller as fragments of JSON nobody can deserialize until the last one.
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

/// <summary>
/// The session of <c>MorganaHostedAgent</c>: it carries the conversation an inbound A2A request
/// belongs to and the system that named it.
/// </summary>
/// <remarks>
/// A2A identifies a conversation by its <c>contextId</c>, which the hosting layer turns into the key
/// it asks a session under. This session is therefore the only place that identity reaches
/// <c>MorganaHostedAgent</c>, which is handed a session but never the request.
/// </remarks>
public sealed class MorganaHostedAgentSession : AgentSession
{
    /// <summary>Morgana conversation this request belongs to.</summary>
    public string ConversationId { get; }

    /// <summary>
    /// System that asked, as its token declared it, or <c>null</c> when the request reached this
    /// agent without one — which the gate in front of the endpoint does not allow.
    /// </summary>
    public string? CallerIssuer { get; }

    /// <summary>Binds the session to the conversation named by the inbound request.</summary>
    /// <param name="conversationId">Conversation to serve on, already scoped to whoever named it.</param>
    /// <param name="callerIssuer">System that asked.</param>
    public MorganaHostedAgentSession(string conversationId, string? callerIssuer = null)
    {
        // Both values are fixed by the store from the request: the agent never chooses the conversation it serves.
        ConversationId = conversationId;
        CallerIssuer = callerIssuer;
    }
}

// The session contract of the hosting layer is published as evaluation-only, so a Morgana that
// serves A2A at all has to take it as it stands: nothing here chooses to use a provisional API.
#pragma warning disable MAAI001

/// <summary>
/// The <see cref="AgentSessionStore"/> of <c>MorganaHostedAgent</c>: turns a request's A2A context
/// id into the conversation it is served on. It stores nothing.
/// </summary>
/// <remarks>
/// Storing nothing is correct, not a shortcut: a Morgana agent's state lives in its actor and,
/// encrypted, in the per-conversation database. A second store would be a copy that silently diverges.
/// </remarks>
public sealed class MorganaHostedAgentSessionStore : AgentSessionStore
{
    /// <summary>
    /// Parts a partner's name from the context id it wrote. Deliberately a character nobody puts in
    /// an issuer name, so two partners cannot be made to agree on one conversation by choosing their
    /// context ids around it.
    /// </summary>
    private const string ForeignConversationSeparator = "~";

    /// <summary>Names the system that asked, taken from the token the gate already validated.</summary>
    private readonly Func<string?> callerIssuerResolver;

    /// <summary>Logger for inbound-request diagnostics.</summary>
    private readonly ILogger logger;

    /// <summary>Builds the store over the means of telling who is asking.</summary>
    /// <param name="callerIssuerResolver">Names the system behind the request being served.</param>
    /// <param name="logger">Logger for inbound-request diagnostics.</param>
    public MorganaHostedAgentSessionStore(Func<string?> callerIssuerResolver, ILogger logger)
    {
        // The issuer is resolved per request because the store is a singleton and serves every partner.
        this.callerIssuerResolver = callerIssuerResolver;
        this.logger = logger;
    }

    /// <inheritdoc />
    public override ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey sessionStoreKey, CancellationToken cancellationToken = default)
    {
        // The issuer is what the gate in front of the endpoint proved, never what the request claims.
        string? callerIssuer = callerIssuerResolver();
        string conversationId = ResolveConversationId(sessionStoreKey.SessionId, callerIssuer);

        // The line that tells an operator which system reached which conversation.
        logger.LogInformation(
            "Inbound A2A request from '{CallerIssuer}' for agent '{AgentName}' on conversation '{ConversationId}'",
            callerIssuer ?? "an undeclared system", agent.Name, conversationId);

        // A fresh session per request: the conversation's state lives in its actor, not here.
        return ValueTask.FromResult<AgentSession?>(new MorganaHostedAgentSession(conversationId, callerIssuer));
    }

    /// <summary>
    /// Decides which conversation an inbound request is served on, which is the whole of what keeps
    /// one caller out of another's.
    /// </summary>
    /// <remarks>
    /// A context id is written by whoever calls. For this installation's own ring that is the point:
    /// a colleague must land on the very conversation the user is having and the id names it. For
    /// anyone else it is a string a stranger chose. Honouring it as a conversation of ours would
    /// let a partner name a live user's — reaching their agents, reading the shared context those
    /// agents were told and spending their budget. So a partner's exchanges are conversations of the
    /// partner: same id, kept apart by the one thing that caller cannot choose.
    /// </remarks>
    /// <param name="sessionStoreId">A2A context id, as the caller wrote it.</param>
    /// <param name="callerIssuer">System that asked.</param>
    private static string ResolveConversationId(string sessionStoreId, string? callerIssuer)
        => callerIssuer is null || string.Equals(callerIssuer, Constants.AgentToAgent.IssuerName, StringComparison.OrdinalIgnoreCase)
            ? sessionStoreId
            : $"{callerIssuer}{ForeignConversationSeparator}{sessionStoreId}";

    /// <inheritdoc />
    public override ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey sessionStoreKey, AgentSession session, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}

#pragma warning restore MAAI001