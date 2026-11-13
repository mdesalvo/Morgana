using System.Security.Cryptography;
using System.Text.Json;
using A2A;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.A2A;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Morgana.AI;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting what this installation does toward a colleague published somewhere else: which cards it accepts, what it signs and where a credential may go.
/// The partner is a stub serving the documents a deployment has to survive (a card naming a third host, one demanding a scheme nobody here presents, one demanding nothing).
/// Deterministic and free, the assertions read the request the stub recorded, since the token on the way out is this installation's and the answer is the peer's.
/// </summary>
public sealed class PeerFederationTests
{
    /// <summary>Partner publishing the colleague, as this installation's configuration names it.</summary>
    private const string PartnerName = "acme";

    /// <summary>Name that partner filed this installation's key under, which its gate expects in <c>iss</c>.</summary>
    private const string PartnerIssuer = "morgana-of-the-nursery";

    /// <summary>Audience that partner validates against, deliberately not the one this installation uses itself.</summary>
    private const string PartnerAudience = "acme.a2a";

    /// <summary>Audience this installation validates its own callers against, which must not travel to a partner.</summary>
    private const string LocalAudience = "morgana.ai";

    /// <summary>Colleague being consulted, published by <see cref="PartnerName"/>.</summary>
    private const string PeerIntent = "shipping";

    /// <summary>Agent on this side doing the asking, which the minted token names as its subject.</summary>
    private const string CallerIntent = "billing";

    /// <summary>Name the standard bearer scheme is declared under on a served card.</summary>
    private const string BearerSchemeName = "bearer";

    [Fact]
    public async Task A_colleague_is_signed_for_under_the_name_and_audience_its_partner_agreed()
    {
        // A partner whose card demands a JWT bearer at the address it is served from.
        using WireMockServer peer = StartPeer(out string peerAddress);
        StubCard(peer, peerAddress, RequireBearer());
        StubConsultationEndpoint(peer);

        // One consultation, so the stub records the token this side minted.
        using ConfigurationAgentDirectoryService directory = BuildDirectory(peerAddress);
        await ConsultAsync(directory);

        // Neither claim is discoverable and neither is this installation's own: a partner files a
        // caller under a name of its choosing and validates an audience it agreed when it cut the key.
        JsonWebToken token = ReadTokenOf(SingleConsultationRequest(peer));
        Assert.Equal(PartnerIssuer, token.Issuer);
        Assert.Contains(PartnerAudience, token.Audiences);
        Assert.DoesNotContain(LocalAudience, token.Audiences);

        // Which agent asked, so what a partner logs is a colleague rather than merely an installation.
        Assert.Equal(CallerIntent, token.Subject);
    }

    [Fact]
    public async Task A_colleague_advertising_an_interface_at_another_host_is_not_reached_at_all()
    {
        // The partner with a third host its card will point at.
        using WireMockServer peer = StartPeer(out string peerAddress);
        using WireMockServer thirdHost = StartPeer(out string thirdHostAddress);

        // The card is fetched open and decides where every later — credentialed — call lands. One
        // naming a host neither installation agreed on would have this side mint a token and hand it
        // to a stranger, so the colleague is refused before a client for it exists.
        StubCard(peer, thirdHostAddress, RequireBearer());
        StubConsultationEndpoint(thirdHost);

        using ConfigurationAgentDirectoryService directory = BuildDirectory(peerAddress);
        AIAgent? colleague = await ResolveAsync(directory);

        // No colleague, while the third host never heard from this installation.
        Assert.Null(colleague);
        Assert.Empty(thirdHost.LogEntries);
    }

    [Fact]
    public async Task A_colleague_demanding_a_scheme_this_installation_cannot_present_costs_only_itself()
    {
        // A partner on a port of its own.
        using WireMockServer peer = StartPeer(out string peerAddress);

        // OAuth2 is a requirement this side has no way to satisfy. The colleague is left unresolved
        // rather than called bare, which would only be refused where it landed.
        StubCard(peer, peerAddress, new AgentCardSecurity(
            Schemes: new Dictionary<string, SecurityScheme>
            {
                ["oauth"] = new SecurityScheme { OAuth2SecurityScheme = new OAuth2SecurityScheme() }
            },
            RequiredSchemeName: "oauth"));

        // The resolution answers nothing instead of throwing, so the asking agent runs without this colleague.
        using ConfigurationAgentDirectoryService directory = BuildDirectory(peerAddress);
        Assert.Null(await ResolveAsync(directory));
    }

    [Fact]
    public async Task A_colleague_requiring_nothing_is_consulted_with_no_token_at_all()
    {
        // A partner on a port of its own.
        using WireMockServer peer = StartPeer(out string peerAddress);

        // An open A2A peer is reachable, which is the whole reason a card's requirements are read
        // rather than assumed: this installation presents what was asked of it and nothing more.
        StubCard(peer, peerAddress, security: null);
        StubConsultationEndpoint(peer);

        // One consultation, whose request must carry no credential at all.
        using ConfigurationAgentDirectoryService directory = BuildDirectory(peerAddress);
        await ConsultAsync(directory);
        Assert.False(SingleConsultationRequest(peer).ContainsKey("Authorization"));
    }

    [Fact]
    public async Task A_colleague_is_described_by_one_reading_of_its_card_however_many_conversations_consult_it()
    {
        // A partner whose card demands a bearer, as a Morgana's does.
        using WireMockServer peer = StartPeer(out string peerAddress);
        StubCard(peer, peerAddress, RequireBearer());
        StubConsultationEndpoint(peer);

        // A card describes an agent rather than a conversation. Read per conversation, a partner would
        // be answering the same question over and over while this side's own first turn waits on it.
        using ConfigurationAgentDirectoryService directory = BuildDirectory(peerAddress);
        await ResolveAsync(directory);
        await ResolveAsync(directory);

        // Two resolutions, one card fetch.
        Assert.Single(peer.LogEntries, entry => entry.RequestMessage!.Path!.EndsWith("agent-card.json", StringComparison.Ordinal));
    }

    /// <summary>Stands a peer up on a port of its own, handing back the address its card is served at.</summary>
    /// <param name="peerAddress">Everything before the published agent path, as configuration would declare it.</param>
    private static WireMockServer StartPeer(out string peerAddress)
    {
        // A free port, so several peers of one test never collide.
        WireMockServer peer = WireMockServer.Start();

        // Without the trailing separator, as a deployment writes a partner's Url.
        peerAddress = peer.Url!.TrimEnd('/');
        return peer;
    }

    /// <summary>
    /// Serves the colleague's card, advertising its endpoint at <paramref name="interfaceAddress"/>.
    /// The address served from and the one advertised are apart because their disagreement is the case worth testing.
    /// </summary>
    /// <param name="peer">The peer serving the card.</param>
    /// <param name="interfaceAddress">Base address the card names its own endpoint under.</param>
    /// <param name="security">What the card demands of a caller; <c>null</c> demands nothing.</param>
    private static void StubCard(WireMockServer peer, string interfaceAddress, AgentCardSecurity? security)
    {
        // The card a Morgana would publish for the shipping agent, its one interface at the given address.
        AgentCard card = new AgentCard
        {
            Name = PeerIntent,
            Description = "Where a parcel is and when it moves.",
            Version = "1.0",
            Capabilities = new AgentCapabilities { Streaming = false, PushNotifications = false },
            Skills = [],
            SupportedInterfaces =
            [
                new AgentInterface
                {
                    Url = $"{interfaceAddress}/a2a/{PeerIntent}",
                    ProtocolBinding = ProtocolBindingNames.JsonRpc
                }
            ]
        };

        // A demanding card defines its schemes and names the one a caller must present.
        if (security is not null)
        {
            card.SecuritySchemes = security.Schemes;
            card.SecurityRequirements =
            [
                new SecurityRequirement
                {
                    Schemes = new Dictionary<string, StringList> { [security.RequiredSchemeName] = new StringList() }
                }
            ];
        }

        // Served at the well-known path, where a caller that knows only the partner's address looks first.
        peer.Given(Request.Create().WithPath($"/a2a/{PeerIntent}/.well-known/agent-card.json").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(card, A2A.A2AJsonUtilities.DefaultOptions)));
    }

    /// <summary>
    /// Accepts the consultation itself, so the request carrying the credential is recorded.
    /// It answers no envelope this side can read, since no assertion here rests on the peer's word.
    /// </summary>
    /// <param name="peer">The peer being consulted.</param>
    private static void StubConsultationEndpoint(WireMockServer peer)
        => peer.Given(Request.Create().WithPath($"/a2a/{PeerIntent}").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("{}"));

    /// <summary>The bearer requirement a Morgana publishes, in the standard form a consumer reads.</summary>
    private static AgentCardSecurity RequireBearer()
        => new AgentCardSecurity(
            Schemes: new Dictionary<string, SecurityScheme>
            {
                [BearerSchemeName] = new SecurityScheme
                {
                    HttpAuthSecurityScheme = new HttpAuthSecurityScheme { Scheme = "bearer", BearerFormat = "JWT" }
                }
            },
            RequiredSchemeName: BearerSchemeName);

    /// <summary>
    /// Builds the directory over a configuration declaring one consultable partner at <paramref name="peerAddress"/>.
    /// The configuration and prompt services are inert stand-ins, since nothing on the path to a colleague elsewhere reads them.
    /// </summary>
    /// <param name="peerAddress">Where the partner answers.</param>
    private static ConfigurationAgentDirectoryService BuildDirectory(string peerAddress)
    {
        // One partner open outbound, with the issuer and audience it agreed, which differ from this installation's own.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Morgana:Authentication:Audience"] = LocalAudience,
                ["Morgana:AgentToAgent:Partners:0:Name"] = PartnerName,
                ["Morgana:AgentToAgent:Partners:0:Url"] = peerAddress,
                ["Morgana:AgentToAgent:Partners:0:SymmetricKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Morgana:AgentToAgent:Partners:0:Enabled"] = "true",
                ["Morgana:AgentToAgent:Partners:0:OutboundPolicy:Enabled"] = "true",
                ["Morgana:AgentToAgent:Partners:0:OutboundPolicy:Issuer"] = PartnerIssuer,
                ["Morgana:AgentToAgent:Partners:0:OutboundPolicy:Audience"] = PartnerAudience
            })
            .Build();

        // The directory every agent of this installation resolves its colleagues through.
        return new ConfigurationAgentDirectoryService(
            new UnreadAgentConfiguration(),
            new UnreadPromptResolver(),
            new ProvidesToolForIntentRegistryService(NullLogger.Instance),
            configuration,
            new FixedHostAddress(),
            new PeerRingKeyService(),
            NullLogger.Instance);
    }

    /// <summary>Resolves the colleague as an agent of this installation would; null when it is refused.</summary>
    /// <param name="directory">Directory under test.</param>
    private static async Task<AIAgent?> ResolveAsync(ConfigurationAgentDirectoryService directory)
        => (await directory.ResolvePeerAgentAsync(new Records.PeerReference(PeerIntent, PartnerName), CallerIntent))?.Agent;

    /// <summary>
    /// Resolves the colleague and puts a question to it, so the peer records the request this installation sends.
    /// The answer is discarded, since the stub answers no envelope and every assertion is on what arrived at the peer.
    /// </summary>
    /// <param name="directory">Directory under test.</param>
    private static async Task ConsultAsync(ConfigurationAgentDirectoryService directory)
    {
        AIAgent colleague = await ResolveAsync(directory)
            ?? throw new InvalidOperationException("The colleague was not resolved, so nothing was ever sent to its peer.");

        // The A2A context id a consultation carries, as an agent of this installation binds it.
        AgentSession session = colleague is A2AAgent a2aColleague
            ? await a2aColleague.CreateSessionAsync("conversation-under-test")
            : await colleague.CreateSessionAsync();

        try
        {
            // The question that makes this installation sign and send its request.
            await colleague.RunAsync("Where is the parcel?", session);
        }
        catch (Exception)
        {
            // The peer answers nothing readable on purpose. What the call was made for is already
            // recorded on its side.
        }
    }

    /// <summary>Headers of the one consultation the peer received.</summary>
    /// <param name="peer">The peer that was consulted.</param>
    private static IDictionary<string, WireMock.Types.WireMockList<string>> SingleConsultationRequest(WireMockServer peer)
        => peer.LogEntries!
               .Single(entry => string.Equals(entry.RequestMessage!.Method, "POST", StringComparison.OrdinalIgnoreCase))
               .RequestMessage!.Headers!;

    /// <summary>Reads back the token this installation minted for a request the peer recorded.</summary>
    /// <param name="headers">Headers of the recorded request.</param>
    private static JsonWebToken ReadTokenOf(IDictionary<string, WireMock.Types.WireMockList<string>> headers)
    {
        // Exactly one credential, presented as a bearer.
        string authorization = Assert.Single(headers["Authorization"]);
        Assert.StartsWith("Bearer ", authorization, StringComparison.Ordinal);

        // The token itself, read without validation: its claims are what is under test.
        return new JsonWebToken(authorization["Bearer ".Length..]);
    }

    /// <summary>What a served card demands of its callers.</summary>
    /// <param name="Schemes">Schemes the card defines.</param>
    /// <param name="RequiredSchemeName">The one it names in its requirement.</param>
    private sealed record AgentCardSecurity(Dictionary<string, SecurityScheme> Schemes, string RequiredSchemeName);

    /// <summary>Stands in for the domain configuration, which describes agents of this installation only.</summary>
    private sealed class UnreadAgentConfiguration : IAgentConfigurationService
    {
        /// <inheritdoc />
        public Task<List<Records.IntentDefinition>> GetIntentsAsync() => Task.FromResult(new List<Records.IntentDefinition>());

        /// <inheritdoc />
        public Task<List<Records.Prompt>> GetAgentPromptsAsync() => Task.FromResult(new List<Records.Prompt>());
    }

    /// <summary>Stands in for prompt resolution, which no path toward a colleague elsewhere reaches.</summary>
    private sealed class UnreadPromptResolver : IPromptResolverService
    {
        /// <summary>Fails the test, since resolving a prompt here would mean the outbound path reads this installation's own agents.</summary>
        public Task<Records.Prompt> ResolveAsync(string promptID)
            => throw new InvalidOperationException($"Prompt '{promptID}' was resolved while consulting a colleague published elsewhere.");
    }

    /// <summary>Reports an address for this installation, which only a colleague of its own ring is reached at.</summary>
    private sealed class FixedHostAddress : IHostAddressService
    {
        /// <summary>An address nothing listens on, never reached since no colleague of the ring is consulted here.</summary>
        public string? ResolveBaseAddress() => "http://127.0.0.1:1";
    }
}