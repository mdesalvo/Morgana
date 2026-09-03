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
/// configuration, and resolves a colleague by fetching its published card over A2A.
/// </summary>
/// <remarks>
/// Nothing is authored twice — an agent announces itself with the prose already written for the
/// classifier and its tools. Resolution goes through the protocol even in-process, which is the
/// point: local and remote colleagues become one code path.
/// </remarks>
public class ConfigurationAgentDirectoryService : IAgentDirectoryService
{
    /// <summary>Version stamped on every locally projected card, tracking the framework's own contract.</summary>
    private const string LocalCardVersion = "1.0";

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
    /// Cards already projected, keyed by intent. Populated on demand rather than at startup because
    /// a conversation consults few agents, and an agent nobody consults never needs a card.
    /// </summary>
    private readonly Dictionary<string, AgentCard?> cardsByIntent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Guards <see cref="cardsByIntent"/>: agents are created concurrently across conversations.</summary>
    private readonly SemaphoreSlim cardsLock = new(1, 1);

    /// <summary>
    /// The one connection pool every call to a colleague goes through, card discovery included.
    /// </summary>
    /// <remarks>
    /// A client is built per colleague per conversation, so a handler per client would open its own
    /// sockets and hold them for as long as the conversation lives. The lifetime bound is the other
    /// half: connections are recycled, so an instance that moves is followed instead of being pinned to
    /// the address it happened to have when it was first reached. This is what a host with
    /// <c>IHttpClientFactory</c> would obtain from it, done here because a library should not grow a
    /// dependency to reach a handler it can simply hold — this service is a singleton, and the pool
    /// lives exactly as long as it does.
    /// </remarks>
    private readonly SocketsHttpHandler connectionPool = new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    /// <summary>Builds the directory over the configuration it projects cards from.</summary>
    /// <param name="agentConfigurationService">Loads the configured intents.</param>
    /// <param name="promptResolverService">Resolves an agent's prompt, and with it its tool definitions.</param>
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
    }

    /// <inheritdoc />
    public async Task<AgentCard?> GetAgentCardAsync(string intent)
    {
        await cardsLock.WaitAsync();
        try
        {
            if (cardsByIntent.TryGetValue(intent, out AgentCard? cached))
                return cached;

            AgentCard? card = await ProjectCardAsync(intent);
            cardsByIntent[intent] = card;

            return card;
        }
        finally
        {
            cardsLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task PublishInterfacesAsync()
    {
        string? baseAddress = hostAddressService.ResolveBaseAddress();
        if (baseAddress is null)
            return;

        await cardsLock.WaitAsync();
        try
        {
            foreach ((string intent, AgentCard? card) in cardsByIntent)
            {
                if (card is not null)
                    card.SupportedInterfaces = [BuildInterface(baseAddress, intent)];
            }
        }
        finally
        {
            cardsLock.Release();
        }

        logger.LogInformation("Agents of this instance publish their A2A interfaces at {BaseAddress}{AgentPathPrefix}/{{intent}}", baseAddress, Constants.AgentToAgent.AgentPathPrefix);
    }

    /// <inheritdoc />
    public async Task<(AIAgent Agent, AgentCard Card)?> ResolvePeerAgentAsync(Records.PeerReference peer, string callerIntent)
    {
        // An agent of this installation is reached where this installation answers, an agent of a
        // peer where that peer was declared to answer. Everything past this point is one path: a
        // card is fetched, read and satisfied the same way whichever side of the boundary it came from.
        Records.ConsultableInstanceOptions? consultableInstance = null;
        string? baseAddress;

        if (peer.Instance is null)
        {
            baseAddress = hostAddressService.ResolveBaseAddress();
            if (baseAddress is null)
            {
                logger.LogError("This instance reports no address it answers on; '{Intent}' cannot be consulted", peer.Intent);
                return null;
            }
        }
        else
        {
            consultableInstance = ResolveConsultableInstances(configuration)
                .FirstOrDefault(candidate => string.Equals(candidate.Name.Trim(), peer.Instance, StringComparison.OrdinalIgnoreCase));

            if (consultableInstance is null)
            {
                logger.LogError("Instance '{Instance}' is not declared under Morgana:AgentToAgent:ConsultableInstances; '{Intent}' cannot be consulted", peer.Instance, peer.Intent);
                return null;
            }

            baseAddress = consultableInstance.Url.TrimEnd('/');
        }

        try
        {
            // Two phases, and the order is the whole point. The card is fetched with no credentials —
            // it is served open precisely so a caller can learn what the endpoint behind it will
            // demand — and only then is a client built to satisfy what it turned out to ask for. It is
            // also fetched rather than read from the projection above: this is the same call a
            // consumer in another process makes, so a card that is unpublished or unreachable fails
            // here, at the directory, rather than at the first consultation.
            A2ACardResolver resolver = new A2ACardResolver(
                new Uri($"{baseAddress}{Constants.AgentToAgent.AgentPathPrefix}/{peer.Intent}/"),
                new HttpClient(connectionPool, disposeHandler: false));

            AgentCard card = await resolver.GetAgentCardAsync();

            HttpClient? peerHttpClient = BuildPeerHttpClient(card, peer, consultableInstance, callerIntent);
            if (peerHttpClient is null)
                return null;

            // Bound to the interface the card advertises, so what this side calls is where the agent
            // said it answers, never a path assembled from an assumption about how it is published.
            AIAgent peerAgent = card.AsAIAgent(peerHttpClient);

            logger.LogInformation(
                "Resolved peer agent '{Intent}' at '{BaseAddress}' for '{CallerIntent}' from its published A2A card",
                peer.Intent, baseAddress, callerIntent);

            return (peerAgent, card);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not resolve peer agent '{Intent}' at '{BaseAddress}' from its published A2A card", peer.Intent, baseAddress);
            return null;
        }
    }

    /// <summary>
    /// The instances this installation may consult, as configuration declares them.
    /// </summary>
    /// <remarks>
    /// Static and public because the startup checks must read the very list resolution reads: a
    /// colleague that validates cleanly and then resolves to nothing on the first conversation is
    /// precisely the silent failure those checks exist to prevent.
    /// </remarks>
    /// <param name="configuration">Application configuration.</param>
    public static List<Records.ConsultableInstanceOptions> ResolveConsultableInstances(IConfiguration configuration)
        => configuration.GetSection("Morgana:AgentToAgent:ConsultableInstances")
               .Get<List<Records.ConsultableInstanceOptions>>() ?? [];

    /// <summary>
    /// Builds the client a colleague is called through, carrying whatever that colleague's own card
    /// declares it requires.
    /// </summary>
    /// <remarks>
    /// Fail-closed on a requirement this side cannot satisfy: a colleague is not called at all rather
    /// than called without the credentials it asked for, and the asking agent runs without it. A card
    /// requiring nothing is called bare, which is what makes an open peer reachable.
    /// </remarks>
    /// <param name="card">The colleague's card, already fetched.</param>
    /// <param name="peer">The colleague being resolved, named in the diagnostics.</param>
    /// <param name="consultableInstance">Declaration of the instance publishing it, or <c>null</c> when it is an agent of this installation.</param>
    /// <param name="callerIntent">Asking agent, recorded as the subject of the minted token.</param>
    /// <returns>The client to call the colleague with, or <c>null</c> when its requirements cannot be met.</returns>
    private HttpClient? BuildPeerHttpClient(
        AgentCard card,
        Records.PeerReference peer,
        Records.ConsultableInstanceOptions? consultableInstance,
        string callerIntent)
    {
        // A2A states alternatives, and satisfying one is enough — so these are candidates to try, not
        // a set to meet.
        List<string> requiredSchemeNames =
            [.. (card.SecurityRequirements ?? []).SelectMany(requirement => requirement.Schemes?.Keys.AsEnumerable() ?? [])];

        if (requiredSchemeNames.Count == 0)
            return new HttpClient(connectionPool, disposeHandler: false);

        foreach (string schemeName in requiredSchemeNames)
        {
            if (card.SecuritySchemes?.TryGetValue(schemeName, out SecurityScheme? securityScheme) != true
                || securityScheme?.HttpAuthSecurityScheme is not { } httpAuthScheme
                || !string.Equals(httpAuthScheme.Scheme, Constants.AgentToAgent.BearerScheme, StringComparison.OrdinalIgnoreCase))
                continue;

            // A instance's key is the one it cut for this caller; this installation's own key is the
            // one it shares with itself. Either way the secret is configured here and never discovered.
            string? symmetricKey = consultableInstance is null
                ? ResolvePeerSigningKey(configuration)
                : consultableInstance.SymmetricKey;

            if (string.IsNullOrWhiteSpace(symmetricKey))
            {
                logger.LogError(
                    "No usable signing key for '{Intent}': declare the '{IssuerName}' issuer under Morgana:Authentication:Issuers for an agent of this installation, or a SymmetricKey on the instance entry for one published elsewhere",
                    peer.Intent, Constants.AgentToAgent.IssuerName);
                return null;
            }

            (string issuer, string audience) = ReadBearerIssuance(card);

            // A instance that cut a key for this caller alone names the issuer it filed that key
            // under, which its public card cannot say without naming it to everyone else too.
            return new HttpClient(
                new MorganaPeerAuthenticationHandler(connectionPool, symmetricKey, consultableInstance?.Issuer ?? issuer, audience, callerIntent),
                disposeHandler: false);
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
        JsonElement? bearerIssuanceParameters = card.Capabilities.Extensions?
            .FirstOrDefault(extension => string.Equals(extension.Uri, Constants.AgentToAgent.BearerIssuanceExtensionUri, StringComparison.OrdinalIgnoreCase))?
            .Params;

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
        List<Records.IntentDefinition> intents = await agentConfigurationService.GetIntentsAsync();

        Records.IntentDefinition? definition = intents.FirstOrDefault(i => string.Equals(i.Name, intent, StringComparison.OrdinalIgnoreCase));
        if (definition is null)
            return null;

        Records.Prompt prompt = await promptResolverService.ResolveAsync(intent);

        // Left empty when the server has not bound yet, which is the normal case: cards are projected
        // while the endpoints are being mapped, and PublishInterfacesAsync fills them in the moment
        // the address is known — before any request can read one.
        string? baseAddress = hostAddressService.ResolveBaseAddress();

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
            // peer issuer may consult this agent, and the card promises nothing finer than that.
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
    /// The audience this installation validates an inbound token against, and therefore the one its
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
    /// standard form by the card's own security requirement, and this says only how to satisfy it.
    /// So a consumer that has never heard of the extension is held to exactly what any A2A consumer
    /// is held to, and one holding a token issued out of band is unaffected. The URI names a
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
        Records.IssuerOptions? issuer = configuration.GetSection("Morgana:Authentication:Issuers")
            .Get<List<Records.IssuerOptions>>()?
            .FirstOrDefault(i => string.Equals(i.Name, Constants.AgentToAgent.IssuerName, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(issuer?.SymmetricKey)
               || string.Equals(issuer.SymmetricKey.Trim(), Constants.Overrides.Secure, StringComparison.Ordinal)
            ? null
            : issuer.SymmetricKey;
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
        /// request-response, and the validator already tolerates 30 seconds of clock skew.
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

        /// <summary>Builds the handler over the key Morgana shares with the agent being called.</summary>
        /// <param name="innerHandler">Shared connection pool this handler sends through; owned by the directory, never by this handler.</param>
        /// <param name="symmetricKey">Signing key of the issuer named below; at least 256 bits.</param>
        /// <param name="issuer">Issuer name to sign under, taken from the callee's own card.</param>
        /// <param name="audience">Audience the receiving instance validates against.</param>
        /// <param name="callerIntent">Intent of the asking agent, recorded as the token's subject.</param>
        public MorganaPeerAuthenticationHandler(HttpMessageHandler innerHandler, string symmetricKey, string issuer, string audience, string callerIntent)
            : base(innerHandler)
        {
            signingCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)),
                SecurityAlgorithms.HmacSha256);

            this.issuer = issuer;
            this.audience = audience;
            this.callerIntent = callerIntent;
        }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken());

            return base.SendAsync(request, cancellationToken);
        }

        /// <summary>Issues a token naming the asking agent, valid for <see cref="TokenLifetime"/>.</summary>
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
