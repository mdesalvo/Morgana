using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
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

    /// <summary>Hosts naming every interface rather than one, which no peer can knock at.</summary>
    private static readonly string[] WildcardHosts = ["+", "*", "0.0.0.0", "[::]", "::"];

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

    /// <summary>Application configuration, read for the partners this installation federates with.</summary>
    private readonly IConfiguration configuration;

    /// <summary>Secret this installation signs consultations between its own agents with.</summary>
    private readonly PeerRingKeyService peerRingKeyService;

    /// <summary>
    /// Tells the directory where this instance answers, so a published card can name a callable
    /// endpoint without anyone configuring the application's own URL.
    /// </summary>
    private readonly IHostAddressService hostAddressService;

    /// <summary>Logger for directory diagnostics.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Wait on the wire: longer than the answering side's own, so that side gives up first and what
    /// comes back is its envelope rather than a cancelled request faulting the turn; shorter than
    /// the turn containing it, so the asking agent is still owed an answer when it gives up.
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
    /// <param name="peerRingKeyService">Holds the secret this installation's own consultations are signed with.</param>
    /// <param name="logger">Logger for directory diagnostics.</param>
    public ConfigurationAgentDirectoryService(
        IAgentConfigurationService agentConfigurationService,
        IPromptResolverService promptResolverService,
        IConfiguration configuration,
        IHostAddressService hostAddressService,
        PeerRingKeyService peerRingKeyService,
        ILogger logger)
    {
        this.agentConfigurationService = agentConfigurationService;
        this.promptResolverService = promptResolverService;
        this.configuration = configuration;
        this.hostAddressService = hostAddressService;
        this.peerRingKeyService = peerRingKeyService;
        this.logger = logger;

        // The same ladder the answering side reads its own step off, so the two cannot disagree about
        // which of them is meant to give up first.
        peerRequestTimeout = Records.PeerConsultationWaits.From(configuration).Caller;
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
            // under a running process. Only where this instance answers is not known at projection
            // time, so that one field is settled below on whichever ask first finds it knowable.
            if (!cardsByIntent.TryGetValue(intent, out AgentCard? card))
            {
                // An intent nobody configured is remembered as such: it stays unconfigured for the
                // process's life, so asking again would re-read configuration to reach the same nothing.
                card = await ProjectCardAsync(intent);
                cardsByIntent[intent] = card;
            }

            return WithPublishedInterface(card, intent);
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

    /// <summary>
    /// Names on a card where this instance answers for it, as soon as that is knowable.
    /// </summary>
    /// <remarks>
    /// A card is projected while the endpoints are still being mapped, before the server has bound
    /// anything, so at that moment there is no address to put on it. Settled on whichever ask first
    /// finds one rather than by a pass over every card at startup: a pass has to run at a moment,
    /// and every moment after the server begins listening is a moment a caller may already be
    /// reading. Asked for again, an address already settled costs a comparison.
    /// </remarks>
    /// <param name="card">Card being served, or <c>null</c> for an intent nobody configured.</param>
    /// <param name="intent">Agent whose endpoint the card names.</param>
    private AgentCard? WithPublishedInterface(AgentCard? card, string intent)
    {
        // Two reasons to hand back what arrived. An intent configured nowhere has no card to put an
        // address on: materialising one here would publish an agent this installation does not hold.
        // A card already naming its interface was settled by an earlier ask, which is the ordinary
        // state of every request after the first — where this instance answers does not move under a
        // running process, so deciding it again could only disagree with what a caller already read.
        if (card is null || card.SupportedInterfaces is { Count: > 0 })
            return card;

        // Still unknown, which is the ordinary state until the server binds: the card goes out
        // saying nothing about where to reach this agent, exactly as it did a moment ago.
        string? baseAddress = hostAddressService.ResolveBaseAddress();
        if (baseAddress is null)
            return card;

        // The card provides a single endpoint for the agent, replacing a complex set of addresses
        // with one interface to evaluate.
        AgentInterface publishedInterface = BuildInterface(baseAddress, intent);
        card.SupportedInterfaces = [publishedInterface];

        // The one line telling an operator where this instance actually answers for this agent, which
        // nothing in configuration states: it was decided by whatever the server bound.
        logger.LogInformation(
            "Agent '{Intent}' of this instance publishes its A2A interface at {InterfaceUrl}", intent, publishedInterface.Url);

        return card;
    }

    /// <inheritdoc />
    public async Task<(AIAgent Agent, AgentCard Card)?> ResolvePeerAgentAsync(Records.PeerReference peer, string callerIntent)
    {
        // Where the colleague answers: this installation's own address, or the one a declared system
        // was given. The instance stays null for one of ours and that null is read again below — it
        // decides whose key signs the call and under whose issuer name.
        Records.PartnerOptions? consultablePartner = null;
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
            // The partner that publishes this colleague, admitted here only while the relationship is
            // live and this direction of it is open. Its name is typed twice by hand — on the attribute
            // in code, on the entry in configuration — so spacing is not allowed to part them.
            consultablePartner = ResolvePartners(configuration)
                .FirstOrDefault(candidate => candidate.Enabled
                                             && candidate.OutboundPolicy?.Enabled == true
                                             && string.Equals(candidate.Name.Trim(), peer.Instance, StringComparison.OrdinalIgnoreCase));

            // Reachable only if configuration says where: unlike its own address, a partner's is declared.
            if (consultablePartner is null)
            {
                logger.LogError(
                    "Partner '{Instance}' is not declared under Morgana:AgentToAgent:Partners, or is not open to being consulted; '{Intent}' cannot be consulted",
                    peer.Instance, peer.Intent);
                return null;
            }

            // The agent path is concatenated with its own leading slash, so a Url written with a
            // trailing one would otherwise produce a double slash in every address built from it.
            baseAddress = consultablePartner.Url.TrimEnd('/');
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
            HttpClient? peerHttpClient = BuildPeerHttpClient(card, baseAddress, peer, consultablePartner, callerIntent);
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
    /// The partners this installation federates with, as configuration declares them.
    /// </summary>
    /// <remarks>
    /// Static and public because the startup checks must read the very list resolution reads: a
    /// colleague that validates cleanly and then resolves to nothing on the first conversation is
    /// precisely the silent failure those checks exist to prevent.
    /// <para>A parked partner is dropped here rather than at each reader, so switching one off closes
    /// both directions at once instead of leaving whichever reader forgot to ask.</para>
    /// </remarks>
    /// <param name="configuration">Application configuration.</param>
    public static List<Records.PartnerOptions> ResolvePartners(IConfiguration configuration)
        => [.. (configuration.GetSection("Morgana:AgentToAgent:Partners").Get<List<Records.PartnerOptions>>() ?? [])
                .Where(partner => partner.Enabled)];

    /// <summary>
    /// The partners admitted to call this installation, under the name their calls arrive with.
    /// </summary>
    /// <remarks>
    /// A partner signing under a name of its own choosing says so on its inbound policy; everyone
    /// else arrives under the name this installation knows it by.
    /// </remarks>
    /// <param name="configuration">Application configuration.</param>
    public static List<(string Issuer, Records.PartnerOptions Partner)> ResolveAdmittedPartners(IConfiguration configuration)
        => [.. ResolvePartners(configuration)
                .Where(partner => partner.InboundPolicy?.Enabled == true)
                // What arrives in a real token's iss claim, so a stray space in configuration must not
                // refuse a caller for a reason nobody can see.
                .Select(partner => ((partner.InboundPolicy!.Issuer ?? partner.Name).Trim(), partner))];

    /// <summary>
    /// The issuers admitted to one published agent, resolved once so a gate need not read
    /// configuration per request.
    /// </summary>
    /// <remarks>
    /// A policy declaring no <c>OnAgents</c> reaches every published agent, which is what a wholly
    /// trusted partner gets. This installation's own agents are admitted to all of them
    /// unconditionally: which colleagues they may consult has one author, <c>[ConsultsAgent]</c>.
    /// </remarks>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="intent">Published agent whose admitted callers are being resolved.</param>
    public static HashSet<string> ResolveAdmittedIssuers(IConfiguration configuration, string intent)
        => new HashSet<string>(
               ResolveAdmittedPartners(configuration)
                   // A null OnAgents list is the policy admitting its partner everywhere; an empty one
                   // admits it nowhere and both are meant — omitting the key is not the same as writing [].
                   .Where(admitted => admitted.Partner.InboundPolicy!.OnAgents is null
                                      || admitted.Partner.InboundPolicy.OnAgents.Any(agent => string.Equals(agent?.Trim(), intent, StringComparison.OrdinalIgnoreCase)))
                   .Select(admitted => admitted.Issuer)
                   .Append(Constants.AgentToAgent.IssuerName),
               StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Refuses an address this installation could not be reached at, declared for the card its
    /// agents publish. Returns silently when nothing is published, or when nothing is declared.
    /// </summary>
    /// <remarks>
    /// The one thing a deployment says about itself and it says it only when the binding cannot:
    /// behind an ingress or a published container port, what Kestrel bound is not where a peer knocks
    /// and a card naming the binding is refused by every consumer. Weighed here because the value is
    /// read at the first card ask, long after a deployer could still be watching for a typo in it.
    /// </remarks>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="publishedIntents">Agents this installation publishes over A2A; empty switches the check off.</param>
    /// <exception cref="InvalidOperationException">The declared address is relative or on a scheme carrying no bearer.</exception>
    public static void ValidatePublishedAddress(IConfiguration configuration, IReadOnlyCollection<string> publishedIntents)
    {
        if (publishedIntents.Count == 0)
            return;

        // Undeclared is the ordinary case: an instance reached at what it bound describes itself from
        // the binding and has nothing to say here.
        string? declaredPublicAddress = configuration["Morgana:AgentToAgent:PublicUrl"];
        if (string.IsNullOrWhiteSpace(declaredPublicAddress))
            return;

        // Everything before the published agent path, which every card built from it appends. A
        // fragment to resolve against something else names no host a peer could knock at.
        if (!Uri.TryCreate(declaredPublicAddress.Trim(), UriKind.Absolute, out Uri? publicAddress))
        {
            throw new InvalidOperationException(
                $"Morgana:AgentToAgent:PublicUrl is '{declaredPublicAddress}', which is not an absolute address. It is where "
                + "peers reach this installation when something in front of it terminates the connection, so it carries a "
                + "scheme and a host: https://morgana.example.com.");
        }

        if (!string.Equals(publicAddress.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(publicAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Morgana:AgentToAgent:PublicUrl declares the scheme '{publicAddress.Scheme}': this installation is reached over http or https.");
        }

        // A host naming every interface rather than one is what the binding already reports and what
        // this declaration exists to replace: published on a card it sends a peer nowhere.
        if (WildcardHosts.Contains(publicAddress.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Morgana:AgentToAgent:PublicUrl declares the host '{publicAddress.Host}', which names every interface rather "
                + "than the one peers reach this installation at. Declare the name they resolve.");
        }
    }

    /// <summary>
    /// Refuses a partner declaration that would admit a caller nobody can prove, or grant a reach
    /// over agents nobody publishes. Throws on the first incoherence; returns silently when nothing
    /// is published.
    /// </summary>
    /// <remarks>
    /// Beside the resolvers it reads, so a check and the runtime depending on it cannot disagree.
    /// All of it guards one shape: a topology that validates cleanly, then fails or opens silently.
    /// Every check weighs one partner entry against itself — what a partner is, where it answers and
    /// how far it reaches are one declaration, so there is no second list left to contradict it.
    /// </remarks>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="publishedIntents">Agents this installation publishes over A2A; empty switches every check off.</param>
    /// <exception cref="InvalidOperationException">Thrown on the first incoherent declaration, naming what to add.</exception>
    public static void ValidateTrustConfiguration(
        IConfiguration configuration,
        IReadOnlyCollection<string> publishedIntents)
    {
        // Nothing published means no door to guard: the whole of this concerns who reaches an agent
        // over A2A and a deployment with the ring down has none.
        if (publishedIntents.Count == 0)
            return;

        // Every partner a deployment wrote down, parked ones included: an entry switched off is still
        // weighed for the name it holds, so reviving it later cannot revive a collision with it.
        List<Records.PartnerOptions> declaredPartners = configuration
            .GetSection("Morgana:AgentToAgent:Partners").Get<List<Records.PartnerOptions>>() ?? [];

        // Names accepted so far, against which each new one must be new.
        HashSet<string> declaredNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Records.PartnerOptions partner in declaredPartners)
        {
            string partnerName = partner.Name?.Trim() ?? string.Empty;

            // Nameless, so there is nothing for an attribute to consult and nothing for an iss claim
            // to match: the entry describes a relationship with nobody.
            if (partnerName.Length == 0)
            {
                throw new InvalidOperationException(
                    "A Morgana:AgentToAgent:Partners entry is missing \"Name\". It is what [ConsultsAgent] writes to reach "
                    + "that partner's desks and the name its own calls arrive under.");
            }

            // Reserved for this installation's own agents, whose consultations are signed with a secret
            // coined at every start. A partner taking the name would have its calls proven against that
            // secret and refused, at runtime, for a reason nothing in configuration shows.
            if (string.Equals(partnerName, Constants.AgentToAgent.IssuerName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Morgana:AgentToAgent:Partners declares a partner named '{Constants.AgentToAgent.IssuerName}', which is "
                    + "reserved for this installation's own agents. Give the partner a name of its own.");
            }

            // Two entries under one name leave the order somebody happened to write them in to decide
            // which key proves a caller and which address a call goes to.
            if (!declaredNames.Add(partnerName))
            {
                throw new InvalidOperationException(
                    $"Morgana:AgentToAgent:Partners declares '{partnerName}' twice. One partner is one entry, "
                    + "carrying its key beside what each direction of the relationship allows.");
            }

            // A parked relationship is not weighed any further: it opens nothing in either direction,
            // so an address or a ceiling it is still missing costs nothing until somebody revives it.
            if (!partner.Enabled)
                continue;

            bool consultable = partner.OutboundPolicy?.Enabled == true;
            bool admitted = partner.InboundPolicy?.Enabled == true;

            // Neither direction open is an entry that reads as a live relationship and is none. Parking
            // one is what "Enabled": false says and it says it where a reader looks first.
            if (!consultable && !admitted)
            {
                throw new InvalidOperationException(
                    $"Partner '{partnerName}' opens neither direction: declare \"OutboundPolicy\": {{ \"Enabled\": true }} to "
                    + "consult its agents, \"InboundPolicy\": { \"Enabled\": true } to let it consult this installation's, "
                    + "or \"Enabled\": false on the partner itself to park the relationship.");
            }

            // The placeholder counts as absent, or an un-overridden deployment signs and proves with
            // the literal word — which fails at the first call rather than here.
            if (string.IsNullOrWhiteSpace(partner.SymmetricKey)
                || string.Equals(partner.SymmetricKey.Trim(), Constants.Overrides.Secure, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Partner '{partnerName}' carries no usable SymmetricKey. It is the one secret the two installations "
                    + "share: calls to that partner are signed with it and calls from it are proven against it. "
                    + "Override it through User Secrets or the environment.");
            }

            // A partner this installation only consults never reaches JWTAuthenticationService, which
            // proves the key of every caller it admits: nothing else would weigh this one until the
            // first consultation tried to sign with it, mid-turn, inside an agent's tool call.
            byte[] symmetricKeyBytes = Encoding.UTF8.GetBytes(partner.SymmetricKey);
            if (symmetricKeyBytes.Length < 32)
            {
                throw new InvalidOperationException(
                    $"Partner '{partnerName}' carries a SymmetricKey of {symmetricKeyBytes.Length * 8} bits: it must be at "
                    + "least 256 bits (32 bytes), the margin HMAC-SHA256 signs and proves a peer token with.");
            }

            if (consultable)
                ValidateConsultableAddress(partnerName, partner.Url);

            if (admitted)
                ValidateAdmission(partnerName, partner.InboundPolicy!, publishedIntents);
        }
    }

    /// <summary>
    /// Refuses an address a signed request could not be sent to.
    /// </summary>
    /// <param name="partnerName">Partner being weighed, named in the diagnostics.</param>
    /// <param name="url">Address declared for it.</param>
    /// <exception cref="InvalidOperationException">The address is missing, relative or on a scheme carrying no bearer.</exception>
    private static void ValidateConsultableAddress(string partnerName, string url)
    {
        // A base address to join with the published agent path, never a fragment to resolve.
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? consultableUrl))
        {
            throw new InvalidOperationException(
                $"Partner '{partnerName}' is open to being consulted but declares no absolute Url. It is everything before "
                + "the published agent path, which is appended from the intent being consulted.");
        }

        // This Url is where a token signed with that partner's key is sent: only the two schemes carrying one.
        if (!string.Equals(consultableUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(consultableUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Partner '{partnerName}' declares the Url scheme '{consultableUrl.Scheme}': a colleague is reached over http or https.");
        }
    }

    /// <summary>
    /// Refuses an admission granted over agents nobody publishes, or granted with nothing bounding it.
    /// </summary>
    /// <param name="partnerName">Partner being weighed, named in the diagnostics.</param>
    /// <param name="inboundPolicy">How far that partner reaches, as declared.</param>
    /// <param name="publishedIntents">Agents this installation publishes over A2A.</param>
    /// <exception cref="InvalidOperationException">The admission names an unpublished agent or carries no ceiling.</exception>
    private static void ValidateAdmission(
        string partnerName,
        Records.PartnerInboundPolicy inboundPolicy,
        IReadOnlyCollection<string> publishedIntents)
    {
        foreach (string admittedAgent in inboundPolicy.OnAgents ?? [])
        {
            // A name this installation publishes nothing under is a permission granted over nothing —
            // most often a typo and read by whoever wrote it as real access.
            if (!publishedIntents.Any(intent => string.Equals(intent, admittedAgent?.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Partner '{partnerName}' is admitted to '{admittedAgent}', which this installation does not publish "
                    + $"(published: {string.Join(", ", publishedIntents)}).");
            }
        }

        // Behind the A2A door a caller names the conversation it is served on, so how many it may open
        // in an hour is the only bound on what it can spend. Nothing reads an absent declaration as
        // licence to spend without limit — a deployment wanting no real bound switches the ceiling off,
        // or writes a generous number and either way it is a sentence somebody wrote.
        if (inboundPolicy.RateLimiting is null)
        {
            throw new InvalidOperationException(
                $"Partner '{partnerName}' is admitted without declaring \"RateLimiting\". Behind the A2A door a caller names "
                + "the conversation it is served on, so how many it may open in a sliding hour is the only bound on what it "
                + "can spend. Declare { \"Enabled\": true, \"MaxConversationsPerHour\": <n> }, or { \"Enabled\": false } to "
                + "say in as many words that this partner opens what it likes.");
        }

        // A ceiling switched on and left without a number bounds nothing while reading as if it did.
        if (inboundPolicy.RateLimiting.Enabled && inboundPolicy.RateLimiting.MaxConversationsPerHour is not > 0)
        {
            throw new InvalidOperationException(
                $"Partner '{partnerName}' declares a rate limit with no positive \"MaxConversationsPerHour\". "
                + "Declare how many conversations it may open within a sliding hour, generous if it is trusted, but declare it.");
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
    /// <param name="consultablePartner">Declaration of the partner publishing it, or <c>null</c> when it is an agent of this installation.</param>
    /// <param name="callerIntent">Asking agent, recorded as the subject of the minted token.</param>
    /// <returns>The client to call the colleague with, or <c>null</c> when its requirements cannot be met.</returns>
    private HttpClient? BuildPeerHttpClient(
        AgentCard card,
        string baseAddress,
        Records.PeerReference peer,
        Records.PartnerOptions? consultablePartner,
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

            // A partner's key is the secret the two installations share; a colleague of this one is
            // reached under the ring's own, coined at start. Neither is ever discovered from a card.
            string? symmetricKey = consultablePartner is null
                ? peerRingKeyService.SymmetricKey
                : consultablePartner.SymmetricKey;

            // The colleague is left unresolved rather than called unsigned. Only a partner can be
            // missing a key here — the ring always has one — so that is what the message names.
            if (string.IsNullOrWhiteSpace(symmetricKey))
            {
                logger.LogError(
                    "No usable signing key for '{Intent}': declare a SymmetricKey on partner '{Partner}' under Morgana:AgentToAgent:Partners",
                    peer.Intent, consultablePartner?.Name);
                return null;
            }

            // Who this installation is to the callee. A colleague of its own knows it by the reserved
            // ring name; a partner knows it by whatever name it filed this caller's key under, which
            // only that partner can say and it says it out of band, when it cuts the key.
            string? issuer = consultablePartner is null
                ? Constants.AgentToAgent.IssuerName
                : consultablePartner.OutboundPolicy?.Issuer?.Trim();

            // Signing under a name the callee never filed produces a token it refuses, so the
            // colleague is left unresolved instead — a missing declaration rather than a 401 per turn.
            if (string.IsNullOrWhiteSpace(issuer))
            {
                logger.LogError(
                    "Partner '{Partner}' requires a bearer but no Issuer is declared on its OutboundPolicy: it is the name that partner knows this installation by and only that partner can say it, so '{Intent}' cannot be consulted",
                    consultablePartner?.Name, peer.Intent);
                return null;
            }

            // The audience the callee validates against. Declared on that partner's entry only when
            // it runs on something other than the shared default, which two installations of Morgana
            // both do until one of them changes it.
            string audience = consultablePartner?.OutboundPolicy?.Audience?.Trim() is { Length: > 0 } declaredAudience
                ? declaredAudience
                : ResolveAudience();

            // The one address this client will ever attach a token for.
            Uri trustedOrigin = new Uri(baseAddress);

            // In clear, the token is replayable for its whole life. Loopback never reaches a wire.
            if (string.Equals(trustedOrigin.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && !trustedOrigin.IsLoopback)
                logger.LogWarning(
                    "Consultations of '{Intent}' at '{BaseAddress}' are signed over plaintext HTTP: the bearer token is replayable by anyone on the path",
                    peer.Intent, baseAddress);

            return new HttpClient(
                new MorganaPeerAuthenticationHandler(connectionPool, symmetricKey, issuer, audience, callerIntent, trustedOrigin, logger),
                disposeHandler: false) { Timeout = peerRequestTimeout };
        }

        logger.LogError(
            "Agent '{Intent}' requires security scheme(s) '{SchemeNames}', none of which this installation can satisfy",
            peer.Intent, string.Join(", ", requiredSchemeNames));

        return null;
    }

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
        // while the endpoints are still being mapped. Whichever ask first finds an address settles it.
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
                PushNotifications = false
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
    /// The audience this installation validates an inbound token against, which is also what a
    /// partner is assumed to validate until its own entry says otherwise. An opaque identifier
    /// compared for equality: it is neither a hostname nor a resource anybody has to own.
    /// </summary>
    private string ResolveAudience()
        => configuration["Morgana:Authentication:Audience"] ?? "morgana.ai";

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