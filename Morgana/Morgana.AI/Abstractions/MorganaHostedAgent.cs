using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Akka.Actor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions;

/// <summary>The <see cref="AIAgent"/> under which one intent is published over A2A, carrying each inbound request to the actor serving that intent.</summary>
// It owns no model and no session: the agent behind it is the same per-conversation actor a user's turn reaches.
public sealed class MorganaHostedAgent : AIAgent
{
    /// <summary>The longest conversation name an inbound request may be served on.</summary>
    // A conversation of this installation is an identifier of a few dozen characters, so the limit costs an honest caller
    // nothing. The name reaches a filesystem, whose own limit on a path component would otherwise be met mid-turn by a
    // caller free to write whatever it likes.
    private const int MaximumConversationNameLength = 128;

    /// <summary>The intent this hosted agent publishes.</summary>
    private readonly string intent;

    /// <summary>What the agent answers for, as the card on the well-known endpoint states it.</summary>
    private readonly string description;

    /// <summary>Maps the intent to its agent type, so the conversation's actor can be found or created.</summary>
    private readonly IAgentRegistryService agentRegistryService;

    /// <summary>Composes the note in front of every question, the one signal telling the answering agent that its reader is a colleague and not the user.</summary>
    private readonly IPromptComposerService promptComposerService;

    /// <summary>Hands back the actor system on the turn that needs it.</summary>
    // The hosted agents are registered before the actor system starts, so the system cannot be handed over at construction.
    private readonly Func<ActorSystem> actorSystemResolver;

    /// <summary>How long the serving actor is waited for before the agent is reported unreachable.</summary>
    private readonly TimeSpan requestTimeout;

    /// <summary>The conversation's ledger, asked what serving one consultation cost so the answer can carry it back.</summary>
    // Every dust question, the reading, the difference and its clamping alike, is the ledger's own and never computed here.
    private readonly IDustLimitService dustLimitService;

    /// <summary>How many conversations the asking partner may still open.</summary>
    // Behind this door the caller names the conversation, so the conversation's own budget bounds the exchange but not the caller.
    private readonly IPeerAdmissionService peerAdmissionService;

    /// <summary>The owner of the conversation's record, asked to open it before the turn runs.</summary>
    // A partner's exchange has no database until one is made and until then nothing that the turn spends is recorded anywhere.
    private readonly IConversationPersistenceService persistenceService;

    /// <summary>Judges a partner's question before any agent here reads it.</summary>
    private readonly IGuardRailService guardRailService;

    /// <summary>Whether a partner's question is judged at all: an installation may serve its partners unscreened.</summary>
    private readonly bool peerGuardEnabled;

    /// <summary>Records the requests that name no conversation or no agent and those that go unanswered.</summary>
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
    /// <param name="actorSystemResolver">Hands back the actor system on the turn that needs it.</param>
    /// <param name="requestTimeout">Maximum wait for the serving actor's answer.</param>
    /// <param name="dustLimitService">Ledger consulted for what the served turn cost.</param>
    /// <param name="peerAdmissionService">Weighs a partner opening a conversation it has not opened before.</param>
    /// <param name="persistenceService">Owner of the conversation's storage, opened before the turn runs.</param>
    /// <param name="guardRailService">Judges a partner's question before the agent reads it.</param>
    /// <param name="peerGuardEnabled">Whether the PeerGuard is switched on for this installation.</param>
    /// <param name="logger">Records the requests that name no conversation or no agent and those that go unanswered.</param>
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
        IGuardRailService guardRailService,
        bool peerGuardEnabled,
        ILogger logger)
    {
        // Everything a request needs is fixed when the intent is published and lives as long as the endpoint.
        this.intent = intent;
        this.description = description;
        this.agentRegistryService = agentRegistryService;
        this.promptComposerService = promptComposerService;
        this.actorSystemResolver = actorSystemResolver;
        this.requestTimeout = requestTimeout;
        this.dustLimitService = dustLimitService;
        this.peerAdmissionService = peerAdmissionService;
        this.persistenceService = persistenceService;
        this.guardRailService = guardRailService;
        this.peerGuardEnabled = peerGuardEnabled;
        this.logger = logger;
    }

    /// <summary>Carries the request to the actor serving this intent in the conversation the session names, answering with the serialized <see cref="Records.PeerConsultationResponse"/>.</summary>
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
        // opens, both after the exchange was admitted and mid-turn. It is refused here in prose instead.
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
        string? callerIntent =
            options?.AdditionalProperties?.TryGetValue(Constants.MessageProperties.CallerIntent, out object? declaredCallerIntent) == true
                ? declaredCallerIntent?.ToString()
                : null;

        // Asked of the registry per request, never assumed from the fact that this endpoint answers.
        // Publication is decided once at startup, while the endpoint is open to anything that speaks
        // A2A — so a request naming an intent this installation no longer serves is an ordinary
        // request with a plain answer, not a fault to throw at a caller mid-turn.
        Type? agentType = agentRegistryService.ResolveAgentFromIntent(intent);

        // An intent nobody serves any longer is answered in the envelope every refusal of this door uses.
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
            // is written on its own entry, so the refusal speaks in this installation's voice.
            if (!await persistenceService.ConversationExistsAsync(hostedAgentSession.ConversationId)
                && hostedAgentSession.CallerIssuer is { } openingIssuer
                && await peerAdmissionService.TryAdmitNewConversationAsync(openingIssuer) is { IsAdmitted: false } refusal)
            {
                return BuildAgentResponseFromMessage(
                    refusal.RefusalMessage ?? $"The agent for '{intent}' cannot take on further conversations right now. Proceed without it.");
            }

            // Opened before the turn, because a partner's exchange has none until now: the ledger is
            // where everything this turn spends is recorded. What is not recorded can be neither
            // reported back to the caller nor held against the budget below.
            await persistenceService.EnsureDatabaseInitializedAsync(hostedAgentSession.ConversationId);

            // A conversation that has already spent its budget buys nothing more, whoever is asking.
            // For a colleague of this installation this is the very budget the user's own turns are
            // held to, reached over A2A instead of through the pipeline and held to it just the same.
            if (await dustLimitService.IsOverBudgetAsync(hostedAgentSession.ConversationId))
            {
                logger.LogWarning(
                    "Hosted agent '{Intent}' refused a request from '{CallerIntent}': conversation '{ConversationId}' is over budget",
                    intent, callerIntent, hostedAgentSession.ConversationId);

                return BuildAgentResponseFromMessage($"The agent for '{intent}' has no budget left on this conversation. Proceed without it.");
            }

            // A partner's question reaches an agent with none of the guards a user's message passes, so it is
            // judged here once admitted and covered by the budget. A colleague of this installation answers
            // from the same ring and is not judged; a caller not proven as the ring is judged as a partner.
            if (peerGuardEnabled
                && !string.Equals(hostedAgentSession.CallerIssuer, Constants.AgentToAgent.IssuerName, StringComparison.OrdinalIgnoreCase))
            {
                // The name the gate proved is what the judgment and the counter read.
                string partnerName = hostedAgentSession.CallerIssuer ?? "an undeclared system";

                // The PeerGuard's verdict on the question.
                Records.GuardRailResult peerQuestionVerdict = await guardRailService.CheckPeerQuestionAsync(
                    hostedAgentSession.ConversationId, partnerName, intent, description, question, cancellationToken);

                // A declined question never reaches the agent.
                if (!peerQuestionVerdict.Compliant)
                {
                    // The refusal by the layer that decided it and by the partner.
                    TagList securityTags = new TagList
                    {
                        { Telemetry.SecurityDoor, Telemetry.SecurityDoors.Peer },
                        { Telemetry.SecurityOutcome, Telemetry.SecurityOutcomes.Rejected },
                        { Telemetry.SecuritySource, peerQuestionVerdict.Source.ToString() },
                        { Telemetry.ConsultationCaller, partnerName },
                        { Telemetry.AgentIntent, intent }
                    };

                    // The technical cause is published only when the question was declined for want of a judgment.
                    if (peerQuestionVerdict.ErrorType is not null)
                        securityTags.Add(Telemetry.ErrorType, peerQuestionVerdict.ErrorType);

                    // Counts the declined question at the partners' door.
                    Telemetry.SecurityEvents.Add(1, securityTags);

                    // The partner reads a plain answer in the envelope of every refusal of this door and no agent is disturbed.
                    return BuildAgentResponseFromMessage($"The agent for '{intent}' declined this question. Proceed without it.");
                }
            }

            // The actor system exists by the time a request arrives, which is why it is asked for only now.
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
            // ledger of its own: the difference is what isolates the answer's own cost.
            double dustBaseline = await dustLimitService.GetConsumedAsync(hostedAgentSession.ConversationId);

            // The wait is the pipeline's own: it ends when the caller stops or when the pipeline's budget runs out.
            using CancellationTokenSource consultationWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            consultationWait.CancelAfter(requestTimeout);

            // Ask, where the pipeline's convention is Tell, because a colleague's answer is read whole by a caller
            // blocked on it. When the wait runs out the colleague's turn is stopped too, landing in the catch
            // below as an answer.
            Records.PeerConsultationResponse peerConsultationResponse = await agentActor.Ask<Records.PeerConsultationResponse>(
                new Records.PeerConsultation(hostedAgentSession.ConversationId, callerIntent, declaredQuestion, TurnCancellation: consultationWait.Token),
                consultationWait.Token);

            // What the answer cost travels back only to a caller that declared itself an agent of a
            // Morgana: anything else that speaks A2A has no ledger to charge it to and would carry a
            // number it cannot read. Unreported, the spend stays on this installation's books, where it is
            // either way: this step only decides whether the asker gets to see it.
            double dustSpent = await dustLimitService.GetConsumedSinceAsync(hostedAgentSession.ConversationId, dustBaseline);

            if (callerIntent is not null)
                peerConsultationResponse = peerConsultationResponse with { DustConsumed = dustSpent };

            // The envelope travels serialized inside an assistant message because that is the only
            // shape A2A carries, but it is data and not prose: what the asking agent receives is a
            // tool result to read and decide against, never something to relay to the user as it stands.
            return new AgentResponse(new ChatMessage(ChatRole.Assistant, SerializePeerConsultationResponse(peerConsultationResponse)));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hosted agent '{Intent}' failed to serve a request from '{CallerIntent}' on conversation '{ConversationId}'", intent, callerIntent, hostedAgentSession.ConversationId);

            // Only an agent that went silent is reported as one. An answer that would not serialize or an
            // actor that could not be reached is a broken colleague rather than a slow one, which the asking
            // model decides against differently. Either way the answer is not coming.
            return BuildAgentResponseFromMessage(ex is AskTimeoutException or OperationCanceledException
                ? $"The agent for '{intent}' did not answer in time. Proceed without it."
                : $"The agent for '{intent}' could not answer. Proceed without it.");
        }
    }

    /// <summary>The streaming form of <see cref="RunCoreAsync"/>, emitting the answer as a single update.</summary>
    // Nobody watches a consultation and the published card declares no streaming: this exists because AIAgent requires it.
    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // The whole consultation runs here and only then is emitted as one update: asking
        // for a stream is not a second path into a colleague, so the Ask, its timeout and the
        // serialized envelope hold identically whichever form the caller picked.
        AgentResponse response = await RunCoreAsync(messages, session, options, cancellationToken);

        // The envelope is the text, so a single update carries it whole: split across updates it
        // would reach the caller as fragments of JSON nobody can deserialize until the last one.
        yield return new AgentResponseUpdate(ChatRole.Assistant, response.Text);
    }

    /// <inheritdoc />
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        // A session without a context id would name no conversation, so only the store creates one.
        => throw new NotSupportedException(
            $"Sessions of the hosted agent '{intent}' are created by {nameof(MorganaHostedAgentSessionStore)} from the A2A context id, never by the agent itself.");

    /// <inheritdoc />
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        // The session carries only the conversation and the caller, which is all a serialized copy needs.
        => ValueTask.FromResult(JsonSerializer.SerializeToElement(session as MorganaHostedAgentSession, jsonSerializerOptions));

    /// <inheritdoc />
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        // A serialized session without a conversation could be served on none, so it is refused.
        => ValueTask.FromResult<AgentSession>(
            serializedState.Deserialize<MorganaHostedAgentSession>(jsonSerializerOptions)
             ?? throw new InvalidOperationException($"The serialized session handed to hosted agent '{intent}' names no conversation."));

    /// <summary>Wraps a message of the framework in the envelope a real answer travels in, so the asking model parses one shape whatever happened.</summary>
    private static AgentResponse BuildAgentResponseFromMessage(string message)
        // A message of the framework is never an agent's completion, so the envelope reports none.
        => new AgentResponse(new ChatMessage(ChatRole.Assistant,
            SerializePeerConsultationResponse(new Records.PeerConsultationResponse(message, false))));

    /// <summary>Renders the envelope that the asking agent's model receives as the tool result.</summary>
    private static string SerializePeerConsultationResponse(Records.PeerConsultationResponse peerConsultationResponse)
        // The asking side reads it with the same options, so the two halves agree on every name.
        => JsonSerializer.Serialize(peerConsultationResponse, Records.DefaultJsonSerializerOptions);
}

/// <summary>The session of <see cref="MorganaHostedAgent"/>, carrying the conversation an inbound A2A request belongs to and the caller that named it.</summary>
// A2A identifies a conversation by its contextId, which the hosting layer turns into the key it asks a session under.
// This session is the only place that identity reaches the hosted agent, which is handed a session but never the request.
public sealed class MorganaHostedAgentSession : AgentSession
{
    /// <summary>The conversation this request is served on.</summary>
    public string ConversationId { get; }

    /// <summary>The caller that asked, as its token declared it; null only for a request that bypassed the gate in front of the endpoint.</summary>
    public string? CallerIssuer { get; }

    /// <summary>Binds the session to the conversation named by the inbound request.</summary>
    /// <param name="conversationId">The conversation to serve on, already scoped to whoever named it.</param>
    /// <param name="callerIssuer">The caller that asked.</param>
    public MorganaHostedAgentSession(string conversationId, string? callerIssuer = null)
    {
        // Both are fixed for the exchange: the conversation was already scoped to the caller by the store.
        ConversationId = conversationId;
        CallerIssuer = callerIssuer;
    }
}

// The session contract of the hosting layer is published as evaluation-only, so a Morgana that
// serves A2A at all has to take it as it stands: nothing here chooses to use a provisional API.
#pragma warning disable MAAI001

/// <summary>The <see cref="AgentSessionStore"/> of <see cref="MorganaHostedAgent"/>, turning a request's A2A context id into the conversation it is served on.</summary>
// It stores nothing on purpose: a Morgana agent's state lives in its actor and, encrypted, in the conversation's database,
// so a second store would be a copy that silently diverges.
public sealed class MorganaHostedAgentSessionStore : AgentSessionStore
{
    /// <summary>Parts a partner's name from the context id it wrote.</summary>
    // A character nobody puts in an issuer name, so two partners cannot be made to agree on one conversation by choosing
    // their context ids around it.
    private const string ForeignConversationSeparator = "~";

    /// <summary>Names the caller that asked, taken from the token the gate already validated.</summary>
    private readonly Func<string?> callerIssuerResolver;

    /// <summary>Records every inbound request with its caller and the conversation it lands on.</summary>
    private readonly ILogger logger;

    /// <summary>Builds the store over the means of telling who is asking.</summary>
    /// <param name="callerIssuerResolver">Names the caller behind the request being served.</param>
    /// <param name="logger">Records every inbound request.</param>
    public MorganaHostedAgentSessionStore(Func<string?> callerIssuerResolver, ILogger logger)
    {
        // The caller is resolved per request, since one store serves every request of the endpoint.
        this.callerIssuerResolver = callerIssuerResolver;
        this.logger = logger;
    }

    /// <inheritdoc />
    public override ValueTask<AgentSession?> GetSessionAsync(AIAgent agent, AgentSessionStoreKey sessionStoreKey, CancellationToken cancellationToken = default)
    {
        // The caller the gate proved for this request decides whose conversation the context id names.
        string? callerIssuer = callerIssuerResolver();
        string conversationId = ResolveConversationId(sessionStoreKey.SessionId, callerIssuer);

        logger.LogInformation(
            "Inbound A2A request from '{CallerIssuer}' for agent '{AgentName}' on conversation '{ConversationId}'",
            callerIssuer ?? "an undeclared system", agent.Name, conversationId);

        // A fresh session per request, since the conversation's state lives in its actor and record.
        return ValueTask.FromResult<AgentSession?>(new MorganaHostedAgentSession(conversationId, callerIssuer));
    }

    /// <summary>Decides which conversation an inbound request is served on, which is the whole of what keeps one caller out of another's.</summary>
    /// <param name="sessionStoreId">The A2A context id as the caller wrote it.</param>
    /// <param name="callerIssuer">The caller that asked.</param>
    private static string ResolveConversationId(string sessionStoreId, string? callerIssuer)
        // A context id is written by whoever calls. For this installation's own ring that is the point: a colleague must
        // land on the very conversation the user is having. For anyone else it is a string a stranger chose: honouring
        // it would let a partner name a live user's conversation, reaching their agents, their shared context and their
        // budget. A partner's exchanges are therefore conversations of the partner, kept apart by the one thing it cannot choose.
        => callerIssuer is null || string.Equals(callerIssuer, Constants.AgentToAgent.IssuerName, StringComparison.OrdinalIgnoreCase)
            ? sessionStoreId
            : $"{callerIssuer}{ForeignConversationSeparator}{sessionStoreId}";

    /// <inheritdoc />
    public override ValueTask SaveSessionAsync(AIAgent agent, AgentSessionStoreKey sessionStoreKey, AgentSession session, CancellationToken cancellationToken = default)
        // The agent's actor has already saved its own session on the conversation's record.
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public override ValueTask DeleteSessionAsync(AIAgent agent, AgentSessionStoreKey sessionStoreKey, CancellationToken cancellationToken = default)
        // The store holds no session to delete: the conversation's record is ended through Morgana's own API, never by a caller over A2A.
        => ValueTask.CompletedTask;
}

#pragma warning restore MAAI001