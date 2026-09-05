using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using A2A;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IAgentDirectoryService"/>: projects each local agent's card from the domain
/// configuration and resolves a colleague by fetching its published card over A2A.
/// </summary>
public class ConfigurationAgentDirectoryService : IAgentDirectoryService
{
    /// <summary>Version stamped on every locally projected card, tracking the framework's own contract.</summary>
    private const string LocalCardVersion = "1.0";

    /// <summary>Margin by which the wait on the wire exceeds the answering side's wait on its actor.</summary>
    private static readonly TimeSpan PeerRequestTimeoutMargin = TimeSpan.FromSeconds(15);

    /// <summary>Wait on a card: a static document with no model behind it, so nothing like a consultation.</summary>
    private static readonly TimeSpan CardDiscoveryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a colleague's published card is reused before it is read from its publisher again.
    /// Long, because a card describes a desk and a desk changes when somebody redeploys it; short
    /// enough that a partner which moved is followed without restarting this installation.
    /// </summary>
    private static readonly TimeSpan PeerCardFreshness = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long a colleague that failed to answer stays reported unreachable before being tried
    /// again. It is what keeps a partner's outage off the first turn of every conversation opened
    /// while it lasts: one turn waits out the silence, the ones behind it are told at once.
    /// </summary>
    private static readonly TimeSpan PeerCardUnreachableWindow = TimeSpan.FromSeconds(60);

    /// <summary>Source of the intents, which carry each agent's name and purpose.</summary>
    private readonly IAgentConfigurationService agentConfigurationService;

    /// <summary>Source of the agent prompts, whose tool definitions become the card's skills.</summary>
    private readonly IPromptResolverService promptResolverService;

    /// <summary>Application configuration, read for the credentials Morgana signs its own requests with.</summary>
    private readonly IConfiguration configuration;

    /// <summary>
    /// Tells the directory where this instance answers, so a published card can name a callable
    /// endpoint without anyone configuring the application's own URL.
    /// </summary>
    private readonly IHostAddressService hostAddressService;

    /// <summary>Logger for directory diagnostics.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Wait on the wire, longer than the answering side's own so that side gives up first: what comes
    /// back is then its envelope, which the model reads, not a cancelled request faulting the turn.
    /// </summary>
    private readonly TimeSpan peerRequestTimeout;

    /// <summary>
    /// Cards already projected, keyed by intent. Populated on demand rather than at startup because
    /// a conversation consults few agents and an agent nobody consults never needs a card.
    /// </summary>
    /// <remarks>
    /// Readable without the lock below, which is what lets a caller with no async seam of its own —
    /// the hosted agent's factory — read a description that has already been projected.
    /// </remarks>
    private readonly ConcurrentDictionary<string, AgentCard?> cardsByIntent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Holds projection to one agent at a time and holds it off entirely while every card is being
    /// given its published address.
    /// </summary>
    private readonly SemaphoreSlim cardsLock = new(1, 1);

    /// <summary>
    /// What each colleague published, keyed by the endpoint it was read from, so the conversations
    /// that follow do not each ask a partner to describe the same desk again.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<PeerCardReading>>> peerCardReadings = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The one connection pool every call to a colleague goes through, card discovery included.
    /// </summary>
    /// <remarks>
    /// A client is built per colleague per conversation, so a handler per client would open its own
    /// sockets and hold them for as long as the conversation lives. The lifetime bound is the other
    /// half: connections are recycled, so an instance that moves is followed instead of being pinned to
    /// the address it happened to have when it was first reached. This is what a host with
    /// <c>IHttpClientFactory</c> would obtain from it, done here because a library should not grow a
    /// dependency to reach a handler it can simply hold — this service is a singleton and the pool
    /// lives exactly as long as it does.
    /// </remarks>
    private readonly SocketsHttpHandler connectionPool = new SocketsHttpHandler
    {
        // Two minutes: long enough that a burst of consultations reuses one socket, short enough that
        // an instance which moves is followed inside a single conversation.
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),

        // A redirect chooses where a credentialed request lands after the origin was checked and the
        // token attached. A peer that has moved says so on its card.
        AllowAutoRedirect = false
    };

    /// <summary>Builds the directory over the configuration it projects cards from.</summary>
    /// <param name="agentConfigurationService">Loads the configured intents.</param>
    /// <param name="promptResolverService">Resolves an agent's prompt and with it its tool definitions.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="hostAddressService">Reports the address this instance answers on.</param>
    /// <param name="logger">Logger for directory diagnostics.</param>
    public ConfigurationAgentDirectoryService(
        IAgentConfigurationService agentConfigurationService,
        IPromptResolverService promptResolverService,
        IConfiguration configuration,
        IHostAddressService hostAddressService,
        ILogger logger)
    {
        this.agentConfigurationService = agentConfigurationService;
        this.promptResolverService = promptResolverService;
        this.configuration = configuration;
        this.hostAddressService = hostAddressService;
        this.logger = logger;

        // The key the answering side reads for its own wait, so the two agree when configured alike.
        peerRequestTimeout = TimeSpan.FromSeconds(
            configuration.GetValue("Morgana:ActorSystem:TimeoutSeconds", 180)) + PeerRequestTimeoutMargin;
    }

    /// <inheritdoc />
    public async Task<AgentCard?> GetAgentCardAsync(string intent)
    {
        // One projection at a time. Two agents built at once would otherwise each read configuration
        // and prompts to describe the same desk, so the wait covers the whole projection below.
        await cardsLock.WaitAsync();

        try
        {
            // A card outlives the ask that built it: its content is configuration, which does not change
            // under a running process. The very instance handed back here is the one later given its
            // published address, so a second copy would describe the same desk at no address at all.
            if (cardsByIntent.TryGetValue(intent, out AgentCard? cached))
                return cached;

            // An intent nobody configured is remembered as such: it stays unconfigured for the process's
            // life, so asking again would re-read configuration to reach the same nothing.
            AgentCard? card = await ProjectCardAsync(intent);
            cardsByIntent[intent] = card;

            return card;
        }
        finally
        {
            // ProjectCardAsync reads configuration and prompts; whatever it throws must not leave every
            // later card resolution waiting on a semaphore nobody will release.
            cardsLock.Release();
        }
    }

    /// <inheritdoc />
    public AgentCard? TryGetProjectedCard(string intent)
        => cardsByIntent.GetValueOrDefault(intent);

    /// <inheritdoc />
    public async Task PublishInterfacesAsync()
    {
        // Nothing to fill in and nothing to fail over: a host that reports no address publishes
        // cards without an interface, which is what they already carry.
        string? baseAddress = hostAddressService.ResolveBaseAddress();
        if (baseAddress is null)
            return;

        // A card can be asked for while this runs. What it mutates is the very instance that ask would
        // receive, so the projection is held off for the length of the walk.
        await cardsLock.WaitAsync();

        try
        {
            // Every cached card, overwritten rather than filled where empty: this runs once Kestrel has
            // bound and it is the first moment any of them can name where it actually answers.
            foreach ((string intent, AgentCard? card) in cardsByIntent)
            {
                // A negatively cached intent — configured nowhere — stays null: there is no card to
                // give an interface to and materialising one here would publish an agent that is not.
                if (card is not null)
                    card.SupportedInterfaces = [BuildInterface(baseAddress, intent)];
            }
        }
        finally
        {
            // Cards are readable again and from here every one of them names where this instance answers.
            cardsLock.Release();
        }

        // The one line telling an operator where this instance actually published, which nothing in
        // configuration states: it was decided by whatever Kestrel bound.
        logger.LogInformation("Agents of this instance publish their A2A interfaces at {BaseAddress}{AgentPathPrefix}/{{intent}}", baseAddress, Constants.AgentToAgent.AgentPathPrefix);
    }

    /// <inheritdoc />
    public async Task<(AIAgent Agent, AgentCard Card)?> ResolvePeerAgentAsync(Records.PeerReference peer, string callerIntent)
    {
        // Where the colleague answers: this installation's own address, or the one a declared system
        // was given. The instance stays null for one of ours and that null is read again below — it
        // decides whose key signs the call and under whose issuer name.
        Records.OutboundSystemOptions? consultableInstance = null;
        string? baseAddress;

        if (peer.Instance is null)
        {
            // Our own address, never configured. Null before Kestrel has bound, which is a colleague
            // resolved too early rather than one that does not exist — hence an error and no colleague.
            baseAddress = hostAddressService.ResolveBaseAddress();
            if (baseAddress is null)
            {
                logger.LogError("This instance reports no address it answers on; '{Intent}' cannot be consulted", peer.Intent);
                return null;
            }
        }
        else
        {
            // The system that publishes this colleague. Its name is typed twice by hand — on the
            // attribute in code, on the entry in configuration — so spacing is not allowed to part them.
            consultableInstance = ResolveOutboundSystems(configuration)
                .FirstOrDefault(candidate => string.Equals(candidate.Name.Trim(), peer.Instance, StringComparison.OrdinalIgnoreCase));

            // Reachable only if configuration says where: unlike its own address, a peer's is declared.
            if (consultableInstance is null)
            {
                logger.LogError("System '{Instance}' is not declared under Morgana:AgentToAgent:OutboundSystems; '{Intent}' cannot be consulted", peer.Instance, peer.Intent);
                return null;
            }

            // The agent path is concatenated with its own leading slash, so a Url written with a
            // trailing one would otherwise produce a double slash in every address built from it.
            baseAddress = consultableInstance.Url.TrimEnd('/');
        }

        // From here one path, whichever side of the boundary the address came from: a card is fetched,
        // read and satisfied identically for a colleague of this installation and for one elsewhere.
        try
        {
            // What the colleague published, as read by whoever got there first: a card describes a
            // desk rather than a conversation, so every conversation reading the same one would be
            // asking a partner the same question over and over while its own first turn waits.
            AgentCard? card = await ReadPeerCardAsync(baseAddress, peer.Intent);

            // The colleague did not answer or answered with something unreadable — reported by the
            // reading itself, which also holds it unreachable for a while rather than making the next
            // conversation wait out the same silence.
            if (card is null)
                return null;

            // Read before anything is signed: this open document decides where every later call lands.
            // First of the two phases — learn what the endpoint demands, then satisfy it.
            if (!DeclaresOnlyInterfacesAt(card, baseAddress, peer))
                return null;

            // Null when the card demands something this installation cannot present — OAuth2, mTLS, an
            // unknown scheme. Refused rather than called bare: an unsigned call would just 401 anyway.
            HttpClient? peerHttpClient = BuildPeerHttpClient(card, baseAddress, peer, consultableInstance, callerIntent);
            if (peerHttpClient is null)
                return null;

            // Bound to the interface the card advertises, so what this side calls is where the agent
            // said it answers, never a path assembled from an assumption about how it is published.
            AIAgent peerAgent = card.AsAIAgent(peerHttpClient);

            // One line per colleague per conversation: the trace an operator reads to see the ring is
            // actually up and the only place the resolved address of a peer is ever recorded.
            logger.LogInformation(
                "Resolved peer agent '{Intent}' at '{BaseAddress}' for '{CallerIntent}' from its published A2A card",
                peer.Intent, baseAddress, callerIntent);

            // The card travels back beside the agent: a colleague published elsewhere has no local
            // projection to describe it by and its description is what the asking model is told.
            return (peerAgent, card);
        }
        catch (Exception ex)
        {
            // A peer that is down, slow or serving an unparseable card costs this colleague and no
            // more — the agent is built without it. Startup already refused what is genuinely wrong.
            logger.LogError(ex, "Could not resolve peer agent '{Intent}' at '{BaseAddress}' from its published A2A card", peer.Intent, baseAddress);
            return null;
        }
    }

    /// <summary>
    /// The systems this installation may consult, as configuration declares them.
    /// </summary>
    /// <remarks>
    /// Static and public because the startup checks must read the very list resolution reads: a
    /// colleague that validates cleanly and then resolves to nothing on the first conversation is
    /// precisely the silent failure those checks exist to prevent.
    /// </remarks>
    /// <param name="configuration">Application configuration.</param>
    public static List<Records.OutboundSystemOptions> ResolveOutboundSystems(IConfiguration configuration)
        => configuration.GetSection("Morgana:AgentToAgent:OutboundSystems").Get<List<Records.OutboundSystemOptions>>() ?? [];

    /// <summary>
    /// How far each admitted system reaches, as configuration declares it.
    /// </summary>
    /// <remarks>
    /// The inbound half of <see cref="ResolveOutboundSystems"/>, read by the gate and by the startup
    /// check alike so the two cannot disagree on what was declared.
    /// </remarks>
    /// <param name="configuration">Application configuration.</param>
    public static List<Records.InboundSystemOptions> ResolveInboundSystems(IConfiguration configuration)
        => configuration.GetSection("Morgana:AgentToAgent:InboundSystems").Get<List<Records.InboundSystemOptions>>() ?? [];

    /// <summary>
    /// The issuers admitted to one published agent, resolved once so a gate need not read
    /// configuration per request.
    /// </summary>
    /// <remarks>
    /// An entry declaring no <c>Agents</c> reaches every published agent — what a wholly trusted peer
    /// gets and what this installation declares about itself.
    /// </remarks>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="intent">Published agent whose admitted callers are being resolved.</param>
    public static HashSet<string> ResolveAdmittedIssuers(IConfiguration configuration, string intent)
        => new HashSet<string>(
               ResolveInboundSystems(configuration)
                   // A null Agents list is the entry admitting its system everywhere; an empty one admits
                   // it nowhere and both are meant — omitting the key is not the same as writing [].
                   .Where(system => system.Agents is null
                                    || system.Agents.Any(agent => string.Equals(agent?.Trim(), intent, StringComparison.OrdinalIgnoreCase)))
                   // What arrives in a real token's iss claim, so a stray space in configuration must not
                   // refuse a caller for a reason nobody can see. The same holds for its casing below.
                   .Select(system => system.Issuer.Trim()),
               StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses a trust configuration that would publish agents nobody can reach, or reach agents
    /// nobody declared. Throws on the first incoherence; returns silently when nothing is published.
    /// </summary>
    /// <remarks>
    /// Beside the resolvers it reads, so a check and the runtime depending on it cannot disagree.
    /// All of it guards one shape: a topology that validates cleanly, then fails or opens silently.
    /// </remarks>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="publishedIntents">Agents this installation publishes over A2A; empty switches every check off.</param>
    /// <param name="consultsLocally">Whether any agent here consults a colleague of this same installation.</param>
    /// <exception cref="InvalidOperationException">Thrown on the first incoherent declaration, naming what to add.</exception>
    public static void ValidateTrustConfiguration(
        IConfiguration configuration,
        IReadOnlyCollection<string> publishedIntents,
        bool consultsLocally)
    {
        // Nothing published means no door to guard: the whole of this concerns who reaches an agent
        // over A2A and a deployment with the ring down has none.
        if (publishedIntents.Count == 0)
            return;

        // Who may knock at all — one registry, read by the channel gate and the A2A gate alike.
        List<Records.IssuerOptions> declaredIssuers = configuration
            .GetSection("Morgana:Authentication:Issuers").Get<List<Records.IssuerOptions>>() ?? [];

        // How far each knocker reaches. Every check below is one list saying something the other
        // contradicts or leaves unsaid.
        List<Records.InboundSystemOptions> inboundSystems = ResolveInboundSystems(configuration);

        // A local consultation leaves over HTTP signed under the "morgana" issuer and comes back in
        // through this installation's own A2A door, so that issuer is needed at both ends. An
        // installation consulting only elsewhere, or nobody, never mints such a token and needs none.
        if (consultsLocally)
        {
            // This installation's own entry among the issuers it admits, weighed for its role below;
            // its key is weighed separately, by the predicate the signing handler shares.
            Records.IssuerOptions? peerIssuer = declaredIssuers.FirstOrDefault(issuer =>
                string.Equals(issuer.Name, Constants.AgentToAgent.IssuerName, StringComparison.OrdinalIgnoreCase));

            // The same predicate the signing handler reads, so a startup that passes cannot be followed
            // by a runtime that finds no key. Undeclared, blank and still-placeholder are all "no key".
            if (ResolvePeerSigningKey(configuration) is null)
            {
                throw new InvalidOperationException(
                    $"Agents of this installation consult colleagues of their own, but no usable signing key is "
                    + $"configured for the '{Constants.AgentToAgent.IssuerName}' issuer: declare it under "
                    + "Morgana:Authentication:Issuers with a real SymmetricKey (User Secrets or environment), "
                    + "or set Morgana:AgentToAgent:Enabled to false to run without peer consultation.");
            }

            // Typed as a channel it would be turned away by the A2A filter, which refuses a channel
            // key at that door: the ring would be configured, signed and refused by its own gate.
            if (peerIssuer?.Type is not Records.IssuerType.System)
            {
                throw new InvalidOperationException(
                    $"Issuer '{Constants.AgentToAgent.IssuerName}' must declare \"Type\": \"system\": it is what this "
                    + "installation signs its own consultations with and they are admitted at the A2A door like any other peer's.");
            }

            // Proving who you are is not being admitted: without an inbound entry the filter's admitted
            // set is empty for every agent and this installation would 401 its own consultations.
            if (!inboundSystems.Any(system =>
                    string.Equals(system.Issuer?.Trim(), Constants.AgentToAgent.IssuerName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Agents of this installation consult colleagues of their own, but '{Constants.AgentToAgent.IssuerName}' "
                    + "is not declared under Morgana:AgentToAgent:InboundSystems: add { \"Issuer\": "
                    + $"\"{Constants.AgentToAgent.IssuerName}\" }}, which admits it to every published agent.");
            }
        }

        // A system declared and then forgotten in InboundSystems would be handed the whole ring by
        // omission and an omission is exactly what nobody notices. So the scope is required of every
        // system and admitting it to everything stays a sentence somebody wrote.
        foreach (Records.IssuerOptions systemIssuer in declaredIssuers.Where(issuer => issuer.Type is Records.IssuerType.System))
        {
            // A system that can prove who it is and reaches nothing: absent from every agent's admitted
            // set, it would be refused at each one for a reason nobody wrote down.
            if (!inboundSystems.Any(system => string.Equals(system.Issuer?.Trim(), systemIssuer.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Issuer '{systemIssuer.Name}' is declared as a system but has no entry under "
                    + "Morgana:AgentToAgent:InboundSystems. Declare how far it reaches — a list of published agents in "
                    + $"\"Agents\", or the entry alone to admit it to all of them ({string.Join(", ", publishedIntents)}).");
            }
        }

        // The reverse direction of the loop above: there, a system with no scope; here, a scope that
        // describes an admission which can never happen. Both leave a topology that reads as intended.
        foreach (Records.InboundSystemOptions inboundSystem in inboundSystems)
        {
            // Empty when the entry names no issuer at all, which the lookup below then fails to match —
            // reported as an undeclared issuer, which is what a nameless entry effectively is.
            string declaredIssuer = inboundSystem.Issuer?.Trim() ?? string.Empty;

            // The identity behind the name this scope claims to narrow; absent when nobody declared it.
            Records.IssuerOptions? scopedIssuer = declaredIssuers.FirstOrDefault(issuer =>
                string.Equals(issuer.Name, declaredIssuer, StringComparison.OrdinalIgnoreCase));

            // Scoping admits nobody: an entry naming an issuer that cannot prove who it is, or one whose
            // key was cut for a channel, describes an admission that will never happen.
            if (scopedIssuer is null)
            {
                throw new InvalidOperationException(
                    $"Morgana:AgentToAgent:InboundSystems declares '{declaredIssuer}', which is not among "
                    + "Morgana:Authentication:Issuers. Scoping narrows a caller that can already prove who it is.");
            }

            // A channel's key opens the conversation API and nothing under /a2a, so listing which agents
            // it reaches describes a reach that key can never have.
            if (scopedIssuer.Type is not Records.IssuerType.System)
            {
                throw new InvalidOperationException(
                    $"Morgana:AgentToAgent:InboundSystems declares '{scopedIssuer.Name}', which is declared as a channel. "
                    + "A caller is a channel or a colleague, never both.");
            }

            // This installation's own topology has one author, [ConsultsAgent], validated at startup.
            // A scope on the "morgana" issuer would be a second author of it, able only to contradict
            // the first — and to do so at runtime, as a 401 on a consultation the plugin declares.
            if (string.Equals(scopedIssuer.Name, Constants.AgentToAgent.IssuerName, StringComparison.OrdinalIgnoreCase)
                && inboundSystem.Agents is not null)
            {
                throw new InvalidOperationException(
                    $"Morgana:AgentToAgent:InboundSystems declares \"Agents\" for '{Constants.AgentToAgent.IssuerName}'. "
                    + "Which colleagues an agent of this installation may consult is declared by [ConsultsAgent] and validated "
                    + "at startup; narrowing it here could only contradict that. Remove \"Agents\" from the entry.");
            }

            foreach (string scopedAgent in inboundSystem.Agents ?? [])
            {
                // A name this installation publishes nothing under is a permission granted over nothing —
                // most often a typo and read by whoever wrote it as real access.
                if (!publishedIntents.Any(intent => string.Equals(intent, scopedAgent?.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"Morgana:AgentToAgent:InboundSystems admits '{scopedIssuer.Name}' to '{scopedAgent}', which this "
                        + $"installation does not publish (published: {string.Join(", ", publishedIntents)}).");
                }
            }
        }
    }


    /// <summary>
    /// Reads a colleague's published card, sharing one reading with every conversation that needs it
    /// while that reading is still worth trusting.
    /// </summary>
    /// <remarks>
    /// A colleague that did not answer comes back as <c>null</c> and is reported unreachable for the
    /// whole of <see cref="PeerCardUnreachableWindow"/>, so an outage is waited out once rather than
    /// by every conversation opened during it.
    /// </remarks>
    /// <param name="baseAddress">Where the colleague answers, this installation's own or a declared system's.</param>
    /// <param name="intent">Colleague whose card is being read.</param>
    /// <returns>The published card, or <c>null</c> when it could not be read.</returns>
    private async Task<AgentCard?> ReadPeerCardAsync(string baseAddress, string intent)
    {
        // The endpoint is the identity, not the intent: the same desk name at two systems is two
        // colleagues and the address is what separates them.
        string endpoint = $"{baseAddress}{Constants.AgentToAgent.AgentPathPrefix}/{intent}";

        // Whoever gets here first asks the colleague; the rest wait on that one reading instead of
        // putting the same question to the same publisher at the same moment.
        Lazy<Task<PeerCardReading>> reading = peerCardReadings.GetOrAdd(endpoint, StartReading);

        PeerCardReading peerCard = await reading.Value;

        // Still worth trusting, either as what the colleague publishes or as the fact that it is not
        // answering.
        if (!peerCard.IsStale)
            return peerCard.Card;

        // Too old to stand. One caller replaces it and whoever loses that race takes what the winner
        // put there — so a colleague is asked once when its reading expires, not once per
        // conversation that finds it expired.
        Lazy<Task<PeerCardReading>> refreshed = StartReading(endpoint);
        if (!peerCardReadings.TryUpdate(endpoint, refreshed, reading))
            refreshed = peerCardReadings.GetOrAdd(endpoint, StartReading);

        // Exactly one further reading is ever waited for here. Should that one already be expiring
        // too — a colleague slower to describe itself than the window it is trusted for — its card is
        // used as it stands: a turn is owed an answer, never an unbounded pursuit of a fresher one.
        return (await refreshed.Value).Card;

        // The reading itself, held back until somebody actually takes it: the one that loses the race
        // above is discarded without ever having troubled the colleague.
        Lazy<Task<PeerCardReading>> StartReading(string _)
            => new Lazy<Task<PeerCardReading>>(() => FetchPeerCardAsync(baseAddress, intent));
    }

    /// <summary>
    /// Asks a colleague to describe itself, over the wire and with no credentials.
    /// </summary>
    /// <remarks>
    /// Never read from the local projection, even for an agent of this installation: this is the same
    /// call a consumer in another process makes, so an unpublished agent fails here and not mid-turn.
    /// </remarks>
    /// <param name="baseAddress">Where the colleague answers.</param>
    /// <param name="intent">Colleague being asked.</param>
    private async Task<PeerCardReading> FetchPeerCardAsync(string baseAddress, string intent)
    {
        try
        {
            // The trailing slash is load-bearing: the well-known path is appended relative to it.
            A2ACardResolver resolver = new A2ACardResolver(
                new Uri($"{baseAddress}{Constants.AgentToAgent.AgentPathPrefix}/{intent}/"),
                new HttpClient(connectionPool, disposeHandler: false) { Timeout = CardDiscoveryTimeout });

            return new PeerCardReading(await resolver.GetAgentCardAsync(), DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            // A colleague that is down, slow or serving an unparseable card is recorded as one and
            // costs nothing further until the window closes: the agents declaring it run without it.
            logger.LogError(
                ex,
                "Could not read the published A2A card of '{Intent}' at '{BaseAddress}'; it stays unreachable for the next {UnreachableSeconds} seconds",
                intent, baseAddress, PeerCardUnreachableWindow.TotalSeconds);

            return new PeerCardReading(null, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>Scheme, host and port: the boundary a credential may cross and nothing wider.</summary>
    /// <param name="origin">Address trusted to receive a token.</param>
    /// <param name="candidate">Address a request is about to be sent to.</param>
    private static bool IsSameOrigin(Uri origin, Uri candidate)
        => string.Equals(origin.Scheme, candidate.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(origin.Host, candidate.Host, StringComparison.OrdinalIgnoreCase)
           && origin.Port == candidate.Port;

    /// <summary>
    /// Refuses a card advertising an interface anywhere but where the card itself was fetched from.
    /// </summary>
    /// <remarks>
    /// <c>AsAIAgent</c> binds to <c>SupportedInterfaces</c>, so an unchecked card naming a third host
    /// would have this side mint its own token and hand it there. Fail-closed: it costs that colleague.
    /// </remarks>
    /// <param name="card">The colleague's card, already fetched.</param>
    /// <param name="baseAddress">Address the card was fetched from.</param>
    /// <param name="peer">The colleague being resolved, named in the diagnostics.</param>
    private bool DeclaresOnlyInterfacesAt(AgentCard card, string baseAddress, Records.PeerReference peer)
    {
        Uri trustedOrigin = new Uri(baseAddress);

        // Nothing to bind to: this one never said where it answers at all.
        if (card.SupportedInterfaces is not { Count: > 0 } declaredInterfaces)
        {
            logger.LogError("Agent '{Intent}' at '{BaseAddress}' publishes a card advertising no interface", peer.Intent, baseAddress);
            return false;
        }

        // Every one, not the one chosen: which interface the client binds to is its own affair.
        foreach (AgentInterface declaredInterface in declaredInterfaces)
        {
            if (!Uri.TryCreate(declaredInterface.Url, UriKind.Absolute, out Uri? declaredUrl) || !IsSameOrigin(trustedOrigin, declaredUrl))
            {
                logger.LogError(
                    "Agent '{Intent}' at '{BaseAddress}' publishes a card advertising the interface '{DeclaredUrl}', which is not where the card itself was served; it will not be consulted",
                    peer.Intent, baseAddress, declaredInterface.Url);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the client a colleague is called through, carrying whatever that colleague's own card
    /// declares it requires.
    /// </summary>
    /// <remarks>
    /// Fail-closed on a requirement this side cannot satisfy: a colleague is not called at all rather
    /// than called without the credentials it asked for and the asking agent runs without it. A card
    /// requiring nothing is called bare, which is what makes an open peer reachable.
    /// </remarks>
    /// <param name="card">The colleague's card, already fetched.</param>
    /// <param name="baseAddress">Address the card was fetched from and the only one a token is ever attached for.</param>
    /// <param name="peer">The colleague being resolved, named in the diagnostics.</param>
    /// <param name="consultableInstance">Declaration of the instance publishing it, or <c>null</c> when it is an agent of this installation.</param>
    /// <param name="callerIntent">Asking agent, recorded as the subject of the minted token.</param>
    /// <returns>The client to call the colleague with, or <c>null</c> when its requirements cannot be met.</returns>
    private HttpClient? BuildPeerHttpClient(
        AgentCard card,
        string baseAddress,
        Records.PeerReference peer,
        Records.OutboundSystemOptions? consultableInstance,
        string callerIntent)
    {
        // A2A states alternatives and satisfying one is enough — so these are candidates to try, not
        // a set to meet.
        List<string> requiredSchemeNames =
            [.. (card.SecurityRequirements ?? []).SelectMany(requirement => requirement.Schemes?.Keys.AsEnumerable() ?? [])];

        // A card demanding nothing is called with no token at all, which is what keeps an open A2A peer
        // reachable by this installation.
        if (requiredSchemeNames.Count == 0)
            return new HttpClient(connectionPool, disposeHandler: false) { Timeout = peerRequestTimeout };

        // The first requirement this side can actually present wins; the others are never reached.
        foreach (string schemeName in requiredSchemeNames)
        {
            // Only a bearer is honoured. A scheme with no definition behind its name, or one asking for
            // OAuth2 or mTLS, is passed over for the next candidate.
            if (card.SecuritySchemes?.TryGetValue(schemeName, out SecurityScheme? securityScheme) != true
                || securityScheme?.HttpAuthSecurityScheme is not { } httpAuthScheme
                || !string.Equals(httpAuthScheme.Scheme, Constants.AgentToAgent.BearerScheme, StringComparison.OrdinalIgnoreCase))
                continue;

            // A instance's key is the one it cut for this caller; this installation's own key is the
            // one it shares with itself. Either way the secret is configured here and never discovered.
            string? symmetricKey = consultableInstance is null
                ? ResolvePeerSigningKey(configuration)
                : consultableInstance.SymmetricKey;

            // The colleague is left unresolved rather than called unsigned and the message names both
            // places a key can be declared, since which one applies depends on where the colleague runs.
            if (string.IsNullOrWhiteSpace(symmetricKey))
            {
                logger.LogError(
                    "No usable signing key for '{Intent}': declare the '{IssuerName}' issuer under Morgana:Authentication:Issuers for an agent of this installation, or a SymmetricKey on the instance entry for one published elsewhere",
                    peer.Intent, Constants.AgentToAgent.IssuerName);
                return null;
            }

            // The claim values the callee validates against, as its own card declares them — never a
            // pair this side assumes.
            (string issuer, string audience) = ReadBearerIssuance(card);

            // The one address this client will ever attach a token for.
            Uri trustedOrigin = new Uri(baseAddress);

            // In clear, the token is replayable for its whole life. Loopback never reaches a wire.
            if (string.Equals(trustedOrigin.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !trustedOrigin.IsLoopback)
                logger.LogWarning(
                    "Consultations of '{Intent}' at '{BaseAddress}' are signed over plaintext HTTP: the bearer token is replayable by anyone on the path",
                    peer.Intent, baseAddress);

            // A instance that cut a key for this caller alone names the issuer it filed that key
            // under, which its public card cannot say without naming it to everyone else too.
            return new HttpClient(
                new MorganaPeerAuthenticationHandler(connectionPool, symmetricKey, consultableInstance?.Issuer ?? issuer, audience, callerIntent, trustedOrigin, logger),
                disposeHandler: false) { Timeout = peerRequestTimeout };
        }

        logger.LogError(
            "Agent '{Intent}' requires security scheme(s) '{SchemeNames}', none of which this installation can satisfy",
            peer.Intent, string.Join(", ", requiredSchemeNames));

        return null;
    }

    /// <summary>
    /// Reads the claims a card asks a caller to mint its token with.
    /// </summary>
    /// <remarks>
    /// A card that does not carry the extension — one published before it existed, or by an
    /// implementation that has never heard of it — is answered with this installation's own values,
    /// which is what the caller assumed unconditionally before the card could say.
    /// </remarks>
    /// <param name="card">The colleague's card, already fetched.</param>
    private (string Issuer, string Audience) ReadBearerIssuance(AgentCard card)
    {
        // The bearer-issuance entry among whatever extensions the publisher declared; absent on a card
        // published before it existed, or by an implementation that never heard of it.
        JsonElement? bearerIssuanceParameters = card.Capabilities.Extensions?
            .FirstOrDefault(extension => string.Equals(extension.Uri, Constants.AgentToAgent.BearerIssuanceExtensionUri, StringComparison.OrdinalIgnoreCase))?
            .Params;

        // Each half is read on its own, so a card may declare one value and leave the other to default.
        return (ReadStringParameter(bearerIssuanceParameters, Constants.AgentToAgent.BearerIssuerParameter) ?? Constants.AgentToAgent.IssuerName,
                ReadStringParameter(bearerIssuanceParameters, Constants.AgentToAgent.BearerAudienceParameter) ?? ResolveAudience());
    }

    /// <summary>
    /// Reads one string parameter out of an extension's free-form parameters, which are whatever the
    /// publisher put there and are therefore never trusted to have a shape.
    /// </summary>
    /// <param name="parameters">The extension's parameters, absent when it declared none.</param>
    /// <param name="parameterName">Parameter to read.</param>
    private static string? ReadStringParameter(JsonElement? parameters, string parameterName)
        => parameters is { ValueKind: JsonValueKind.Object } parametersElement
           && parametersElement.TryGetProperty(parameterName, out JsonElement value)
           && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>
    /// Builds one card from the intent and prompt declared for <paramref name="intent"/>, including
    /// the interface under which this installation publishes that agent.
    /// </summary>
    /// <param name="intent">Intent to project.</param>
    /// <returns>The card, or <c>null</c> when no intent by that name is configured.</returns>
    private async Task<AgentCard?> ProjectCardAsync(string intent)
    {
        // The domain's own intent list: what an installation can publish is what its plugins declare.
        List<Records.IntentDefinition> intents = await agentConfigurationService.GetIntentsAsync();

        // No entry under that name means there is no agent to describe — unreachable, not broken.
        Records.IntentDefinition? definition = intents.FirstOrDefault(i => string.Equals(i.Name, intent, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            return null;

        // The prose the agent was authored with: its ConsultMeFor becomes the card's description and its
        // tool definitions become the skills, so nothing about this desk is written twice.
        Records.Prompt prompt = await promptResolverService.ResolveAsync(intent);

        // Left empty when the server has not bound yet, which is the normal case: cards are projected
        // while the endpoints are being mapped and PublishInterfacesAsync fills them in the moment
        // the address is known — before any request can read one.
        string? baseAddress = hostAddressService.ResolveBaseAddress();

        // Everything a stranger needs to decide whether to ask this desk and how to be let in.
        return new AgentCard
        {
            Name = definition.Name,

            // The agent's own address to whoever might consult it. The intent description stands in
            // when there is none, but it is a routing phrase written for the classifier — it tells a
            // caller which user utterances land here, never what this desk answers for.
            Description = string.IsNullOrWhiteSpace(prompt.ConsultMeFor) ? definition.Description : prompt.ConsultMeFor,
            Version = LocalCardVersion,
            Skills = ProjectSkills(prompt),

            Capabilities = new AgentCapabilities
            {
                Streaming = false,
                PushNotifications = false,

                // How to mint the token the requirement below asks for. The scheme states that a
                // bearer is needed and stops there, leaving the two claim values this installation
                // validates to be agreed out of band — which is the coupling discovery exists to remove.
                Extensions = [BuildBearerIssuanceExtension()]
            },

            // Bearer, in the standard form every A2A consumer reads. The card is what tells a caller
            // how to pass the gate the endpoints behind it already apply, which is exactly why it is
            // itself served open: a discovery document that required a token could not be discovered.
            SecuritySchemes = new Dictionary<string, SecurityScheme>
            {
                [Constants.AgentToAgent.BearerSchemeName] = new SecurityScheme
                {
                    HttpAuthSecurityScheme = new HttpAuthSecurityScheme
                    {
                        Scheme = Constants.AgentToAgent.BearerScheme,
                        BearerFormat = Constants.AgentToAgent.BearerFormat,
                        Description = "Short-lived token signed with the key this installation shares with its peers."
                    }
                }
            },

            // The requirement names no scopes, because none are honoured: a caller proven to be the
            // peer issuer may consult this agent and the card promises nothing finer than that.
            SecurityRequirements =
            [
                new SecurityRequirement
                {
                    Schemes = new Dictionary<string, StringList>
                    {
                        [Constants.AgentToAgent.BearerSchemeName] = new StringList()
                    }
                }
            ],

            // Where this installation answers for the agent.
            SupportedInterfaces = baseAddress is null ? [] : [BuildInterface(baseAddress, intent)]
        };
    }

    /// <summary>Turns an agent's declared domain tools into the skills its card advertises.</summary>
    /// <remarks>
    /// Reserved framework tools are absent by construction, being declared in <c>morgana.json</c>
    /// rather than in the agent's own prompt. An MCP-only agent advertises no skills, honestly: its
    /// competences are known only once its servers answer. These reach an external consumer of the
    /// card and nobody else: a sibling agent is offered its colleague's ConsultMeFor, never this
    /// inventory, which invites the caller to rule out a question the colleague has never seen.
    /// </remarks>
    /// <param name="prompt">The agent's already-resolved prompt.</param>
    private static List<A2A.AgentSkill> ProjectSkills(Records.Prompt prompt)
    {
        return
        [
            .. prompt.GetAdditionalPropertyOrDefault<Records.ToolDefinition[]>("Tools", [])
                .Select(tool => new A2A.AgentSkill
                {
                    Id = tool.Name,
                    Name = tool.Name,
                    Description = tool.Description
                })
        ];
    }

    /// <summary>
    /// Builds the interface entry naming where one agent of this instance answers.
    /// </summary>
    /// <param name="baseAddress">Address this instance answers on.</param>
    /// <param name="intent">Intent whose endpoint is being named.</param>
    private static AgentInterface BuildInterface(string baseAddress, string intent)
        => new AgentInterface
        {
            Url = $"{baseAddress}{Constants.AgentToAgent.AgentPathPrefix}/{intent}",
            ProtocolBinding = ProtocolBindingNames.JsonRpc
        };

    /// <summary>
    /// The audience this installation validates an inbound token against and therefore the one its
    /// cards ask a caller to name. An opaque identifier compared for equality: it is neither a
    /// hostname nor a resource anybody has to own.
    /// </summary>
    private string ResolveAudience()
        => configuration["Morgana:Authentication:Audience"] ?? "morgana.ai";

    /// <summary>
    /// Builds the extension by which the card declares the two claim values a caller must mint its
    /// bearer token with.
    /// </summary>
    /// <remarks>
    /// Declared as not required, deliberately: the obligation to authenticate is already stated in
    /// standard form by the card's own security requirement and this says only how to satisfy it.
    /// So a consumer that has never heard of the extension is held to exactly what any A2A consumer
    /// is held to and one holding a token issued out of band is unaffected. The URI names a
    /// published specification because it is read on somebody else's card, by an implementation that
    /// will never see this code.
    /// </remarks>
    private AgentExtension BuildBearerIssuanceExtension()
        => new AgentExtension
        {
            Uri = Constants.AgentToAgent.BearerIssuanceExtensionUri,
            Description = "Issuer and audience a caller must mint its bearer token under.",
            Required = false,
            Params = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                [Constants.AgentToAgent.BearerIssuerParameter] = Constants.AgentToAgent.IssuerName,
                [Constants.AgentToAgent.BearerAudienceParameter] = ResolveAudience()
            })
        };

    /// <summary>
    /// The key Morgana signs its own peer traffic with, or <c>null</c> when the <c>morgana</c> issuer
    /// is undeclared, blank, or still holding the placeholder <c>appsettings.json</c> ships.
    /// </summary>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The signing key, or null when it has not been configured.</returns>
    public static string? ResolvePeerSigningKey(IConfiguration configuration)
    {
        // This installation's own entry among the issuers it admits: it signs its internal consultations
        // with the very key it validates them against when they knock back at its own door.
        Records.IssuerOptions? issuer = configuration.GetSection("Morgana:Authentication:Issuers")
            .Get<List<Records.IssuerOptions>>()?
            .FirstOrDefault(i => string.Equals(i.Name, Constants.AgentToAgent.IssuerName, StringComparison.OrdinalIgnoreCase));

        // The placeholder appsettings.json ships counts as no key: a deployment that never overrode it is
        // unconfigured, not one that signs its consultations with the literal word.
        return string.IsNullOrWhiteSpace(issuer?.SymmetricKey)
               || string.Equals(issuer.SymmetricKey.Trim(), Constants.Overrides.Secure, StringComparison.Ordinal)
            ? null
            : issuer.SymmetricKey;
    }

    /// <summary>
    /// One reading of a colleague's published card, with the moment it was taken.
    /// </summary>
    /// <remarks>
    /// A null card is a reading that failed and is kept as deliberately as a successful one: it is
    /// what holds a colleague that is not answering away from the turn of the next conversation.
    /// </remarks>
    /// <param name="Card">What the colleague published, or <c>null</c> when it could not be read.</param>
    /// <param name="ReadAt">When the reading was taken, which is what makes it expire.</param>
    private sealed record PeerCardReading(AgentCard? Card, DateTimeOffset ReadAt)
    {
        /// <summary>
        /// Whether this reading has to be taken again. A colleague that answered is trusted far
        /// longer than one that did not: the first describes a desk, the second an outage.
        /// </summary>
        public bool IsStale
            => DateTimeOffset.UtcNow - ReadAt > (Card is null ? PeerCardUnreachableWindow : PeerCardFreshness);
    }

    /// <summary>
    /// Signs every outbound A2A request with a short-lived token Morgana issues to itself, so an
    /// agent consulting a colleague passes the same gate as any other caller.
    /// </summary>
    /// <remarks>
    /// Per request, not once on the client: an <c>A2AAgent</c> keeps its <see cref="HttpClient"/> for
    /// the whole conversation, far longer than a token should live. Morgana appears in its own issuer
    /// list like a channel does, so protecting reachable endpoints costs one entry and no new concept.
    /// One of these is built per colleague, all of them over the same shared pool, which is why the
    /// inner handler arrives from outside and is never disposed with the client that used it.
    /// </remarks>
    private sealed class MorganaPeerAuthenticationHandler : DelegatingHandler
    {
        /// <summary>
        /// Lifetime of a minted token. Deliberately short: a consultation is a single
        /// request-response and the validator already tolerates 30 seconds of clock skew.
        /// </summary>
        private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

        /// <summary>Signing credentials built once from the issuer's shared symmetric key.</summary>
        private readonly SigningCredentials signingCredentials;

        /// <summary>Audience the receiving Morgana validates against.</summary>
        private readonly string audience;

        /// <summary>Issuer name the receiving side expects, as its own card declared it.</summary>
        private readonly string issuer;

        /// <summary>Intent of the agent requests are signed on behalf of, carried as the subject claim.</summary>
        private readonly string callerIntent;

        /// <summary>The only origin a token is attached for: where the colleague's card was fetched from.</summary>
        private readonly Uri trustedOrigin;

        /// <summary>Records a request that asked for a credential it was not going to be given.</summary>
        private readonly ILogger logger;

        /// <summary>Builds the handler over the key Morgana shares with the agent being called.</summary>
        /// <param name="innerHandler">Shared connection pool this handler sends through; owned by the directory, never by this handler.</param>
        /// <param name="symmetricKey">Signing key of the issuer named below; at least 256 bits.</param>
        /// <param name="issuer">Issuer name to sign under, taken from the callee's own card.</param>
        /// <param name="audience">Audience the receiving instance validates against.</param>
        /// <param name="callerIntent">Intent of the asking agent, recorded as the token's subject.</param>
        /// <param name="trustedOrigin">The only origin a token is attached for.</param>
        /// <param name="logger">Logger for a request leaving the trusted origin.</param>
        public MorganaPeerAuthenticationHandler(HttpMessageHandler innerHandler, string symmetricKey, string issuer, string audience, string callerIntent, Uri trustedOrigin, ILogger logger)
            : base(innerHandler)
        {
            // The key never changes, so the credentials are derived here instead of re-hashing the same
            // secret on every consultation. What is minted per request is the token, not this.
            signingCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)),
                SecurityAlgorithms.HmacSha256);

            this.issuer = issuer;
            this.audience = audience;
            this.callerIntent = callerIntent;
            this.trustedOrigin = trustedOrigin;
            this.logger = logger;
        }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The last word, not the first: the card's interfaces were checked before this client
            // existed. The request still travels — only the credential stays home.
            if (request.RequestUri is not null && !IsSameOrigin(trustedOrigin, request.RequestUri))
            {
                logger.LogError(
                    "A consultation on behalf of '{CallerIntent}' was directed at '{RequestUri}', outside the origin '{TrustedOrigin}' its colleague was resolved at: it travels unsigned",
                    callerIntent, request.RequestUri, trustedOrigin);

                return base.SendAsync(request, cancellationToken);
            }

            // A token minted for this one request and naming the asking desk, now that the destination is
            // known to be the colleague's own.
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());

            return base.SendAsync(request, cancellationToken);
        }

        /// <summary>Issues a token naming the asking agent, valid for <see cref="TokenLifetime"/>.</summary>
        /// <remarks>
        /// The subject is the asking agent's intent, so what the callee logs and traces is which desk
        /// asked, not merely which installation. Neither claim is read as a permission by either side.
        /// </remarks>
        private string MintToken()
            => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Expires = DateTime.UtcNow.Add(TokenLifetime),
                SigningCredentials = signingCredentials,
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = callerIntent,
                    ["name"] = $"Morgana ({callerIntent})"
                }
            });
    }
}