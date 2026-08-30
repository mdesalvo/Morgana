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
    public async Task<AIAgent?> ResolvePeerAgentAsync(string intent, string callerIntent)
    {
        string? baseAddress = hostAddressService.ResolveBaseAddress();
        if (baseAddress is null)
        {
            logger.LogError("This instance reports no address it answers on; '{Intent}' cannot be consulted", intent);
            return null;
        }

        (string? symmetricKey, string audience) = ResolvePeerCredentials();
        if (symmetricKey is null)
        {
            logger.LogError(
                "Peer consultation needs an issuer named '{IssuerName}' under Morgana:Authentication:Issuers to sign its own requests; '{Intent}' cannot be consulted",
                Constants.AgentToAgent.IssuerName, intent);
            return null;
        }

        try
        {
            // The card is fetched rather than read from the projection above, deliberately: this is
            // the same call a consumer in another process makes, so a card that is unpublished or
            // unreachable fails here, at the directory, rather than at the first consultation.
            HttpClient httpClient = new HttpClient(new MorganaPeerAuthenticationHandler(symmetricKey, audience, callerIntent));
            A2ACardResolver resolver = new A2ACardResolver(new Uri($"{baseAddress}{Constants.AgentToAgent.AgentPathPrefix}/{intent}/"), httpClient);

            AIAgent peerAgent = await resolver.GetAIAgentAsync(httpClient);

            logger.LogInformation("Resolved peer agent '{Intent}' for '{CallerIntent}' from its published A2A card", intent, callerIntent);

            return peerAgent;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not resolve peer agent '{Intent}' from its published A2A card", intent);
            return null;
        }
    }

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

            // A consultation is a single question answered in full: the answering turn has no user
            // watching it, and there is nobody to push anything to.
            Capabilities = new AgentCapabilities { Streaming = false, PushNotifications = false },

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
    /// Resolves the key Morgana signs its own peer traffic with, and the audience the receiving side
    /// validates against.
    /// </summary>
    /// <returns>The issuer's key (null when the issuer is not declared) and the configured audience.</returns>
    private (string? SymmetricKey, string Audience) ResolvePeerCredentials()
        => (ResolvePeerSigningKey(configuration),
            configuration["Morgana:Authentication:Audience"] ?? "morgana.ai");

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

        /// <summary>Intent of the agent requests are signed on behalf of, carried as the subject claim.</summary>
        private readonly string callerIntent;

        /// <summary>Builds the handler over the key Morgana shares with itself.</summary>
        /// <param name="symmetricKey">Signing key of the <c>morgana</c> issuer; at least 256 bits.</param>
        /// <param name="audience">Audience the receiving instance validates against.</param>
        /// <param name="callerIntent">Intent of the asking agent, recorded as the token's subject.</param>
        public MorganaPeerAuthenticationHandler(string symmetricKey, string audience, string callerIntent)
            : base(new HttpClientHandler())
        {
            signingCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)),
                SecurityAlgorithms.HmacSha256);

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
                Issuer = Constants.AgentToAgent.IssuerName,
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
