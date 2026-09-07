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
/// The group asserting what this installation does toward a colleague published somewhere else: which
/// cards it accepts, what it signs and where a credential is allowed to go.
/// </summary>
/// <remarks>
/// <para>Kin to <c>AgentCardTests</c> and <c>StartupValidationTests</c>: deterministic, no model, no
/// cost. Where those read what a stranger fetches from this installation and what it refuses to
/// become, this one reads the other direction — the half no HTTP call from outside can reach, since
/// it only runs when this installation is the one asking.</para>
///
/// <para>The partner is stood up here rather than being a second Morgana: what is under test is what
/// <b>leaves</b> — the card this side accepts, the token it mints and the origin it attaches it to —
/// and a real peer would answer those questions with its own behaviour instead of with the cases a
/// deployment actually has to survive. A card naming a third host, one demanding a scheme nobody here
/// can present, one demanding nothing: each is a document, and a document is what this group serves.</para>
///
/// <para>Assertions read the request the peer recorded rather than the answer this side received. A
/// consultation's answer is the peer's word; the token on the way in is ours, and it is the only part
/// a deployment is exposed by. So a run that ends in an exception because the stub answered no proper
/// envelope still proves what it was asked to prove, which is why the calls below are made without
/// expecting one.</para>
/// </remarks>
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

    /// <summary>Desk on this side doing the asking, which the minted token names as its subject.</summary>
    private const string CallerIntent = "billing";

    /// <summary>Name the standard bearer scheme is declared under on a served card.</summary>
    private const string BearerSchemeName = "bearer";

    [Fact]
    public async Task A_colleague_is_signed_for_under_the_name_and_audience_its_partner_agreed()
    {
        using WireMockServer peer = StartPeer(out string peerAddress);
        StubCard(peer, peerAddress, RequireBearer());
        StubConsultationEndpoint(peer);

        await ConsultAsync(BuildDirectory(peerAddress));

        // Neither claim is discoverable and neither is this installation's own: a partner files a
        // caller under a name of its choosing and validates an audience it agreed when it cut the key.
        JsonWebToken token = ReadTokenOf(SingleConsultationRequest(peer));

        Assert.Equal(PartnerIssuer, token.Issuer);
        Assert.Contains(PartnerAudience, token.Audiences);
        Assert.DoesNotContain(LocalAudience, token.Audiences);

        // Which desk asked, so what a partner logs is a colleague rather than merely an installation.
        Assert.Equal(CallerIntent, token.Subject);
    }

    [Fact]
    public async Task A_colleague_advertising_an_interface_at_another_host_is_not_reached_at_all()
    {
        using WireMockServer peer = StartPeer(out string peerAddress);
        using WireMockServer thirdHost = StartPeer(out string thirdHostAddress);

        // The card is fetched open and decides where every later — credentialed — call lands. One
        // naming a host neither installation agreed on would have this side mint a token and hand it
        // to a stranger, so the colleague is refused before a client for it exists.
        StubCard(peer, thirdHostAddress, RequireBearer());
        StubConsultationEndpoint(thirdHost);

        AIAgent? colleague = await ResolveAsync(BuildDirectory(peerAddress));

        Assert.Null(colleague);
        Assert.Empty(thirdHost.LogEntries);
    }

    [Fact]
    public async Task A_colleague_demanding_a_scheme_this_installation_cannot_present_costs_only_itself()
    {
        using WireMockServer peer = StartPeer(out string peerAddress);

        // OAuth2 is a requirement this side has no way to satisfy. The colleague is left unresolved
        // rather than called bare, which would only be refused where it landed.
        StubCard(peer, peerAddress, new AgentCardSecurity(
            Schemes: new Dictionary<string, SecurityScheme>
            {
                ["oauth"] = new SecurityScheme { OAuth2SecurityScheme = new OAuth2SecurityScheme() }
            },
            RequiredSchemeName: "oauth"));

        Assert.Null(await ResolveAsync(BuildDirectory(peerAddress)));
    }

    [Fact]
    public async Task A_colleague_requiring_nothing_is_consulted_with_no_token_at_all()
    {
        using WireMockServer peer = StartPeer(out string peerAddress);

        // An open A2A peer is reachable, which is the whole reason a card's requirements are read
        // rather than assumed: this installation presents what was asked of it and nothing more.
        StubCard(peer, peerAddress, security: null);
        StubConsultationEndpoint(peer);

        await ConsultAsync(BuildDirectory(peerAddress));

        Assert.False(SingleConsultationRequest(peer).ContainsKey("Authorization"));
    }

    [Fact]
    public async Task A_colleague_is_described_by_one_reading_of_its_card_however_many_conversations_consult_it()
    {
        using WireMockServer peer = StartPeer(out string peerAddress);
        StubCard(peer, peerAddress, RequireBearer());
        StubConsultationEndpoint(peer);

        // A card describes a desk rather than a conversation. Read per conversation, a partner would
        // be answering the same question over and over while this side's own first turn waits on it.
        ConfigurationAgentDirectoryService directory = BuildDirectory(peerAddress);
        await ResolveAsync(directory);
        await ResolveAsync(directory);

        Assert.Single(peer.LogEntries, entry => entry.RequestMessage!.Path!.EndsWith("agent-card.json", StringComparison.Ordinal));
    }

    /// <summary>Stands a peer up on a port of its own, handing back the address its card is served at.</summary>
    /// <param name="peerAddress">Everything before the published agent path, as configuration would declare it.</param>
    private static WireMockServer StartPeer(out string peerAddress)
    {
        WireMockServer peer = WireMockServer.Start();
        peerAddress = peer.Url!.TrimEnd('/');
        return peer;
    }

    /// <summary>Serves the colleague's card, advertising its endpoint at <paramref name="interfaceAddress"/>.</summary>
    /// <remarks>
    /// The address the card is served from and the one it advertises are separate parameters because
    /// their disagreement is the case worth testing: a card may only send this installation where it
    /// already was.
    /// </remarks>
    /// <param name="peer">The peer serving the card.</param>
    /// <param name="interfaceAddress">Base address the card names its own endpoint under.</param>
    /// <param name="security">What the card demands of a caller, or <c>null</c> to demand nothing.</param>
    private static void StubCard(WireMockServer peer, string interfaceAddress, AgentCardSecurity? security)
    {
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
                    Url = $"{interfaceAddress}{Constants.AgentToAgent.AgentPathPrefix}/{PeerIntent}",
                    ProtocolBinding = ProtocolBindingNames.JsonRpc
                }
            ]
        };

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

        peer.Given(Request.Create().WithPath($"{Constants.AgentToAgent.AgentPathPrefix}/{PeerIntent}/.well-known/agent-card.json").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(card, A2A.A2AJsonUtilities.DefaultOptions)));
    }

    /// <summary>Accepts the consultation itself, so the request carrying the credential is recorded.</summary>
    /// <remarks>
    /// What it answers is deliberately not an envelope this side can read: the answer is the peer's
    /// word and no assertion here rests on it.
    /// </remarks>
    /// <param name="peer">The peer being consulted.</param>
    private static void StubConsultationEndpoint(WireMockServer peer)
        => peer.Given(Request.Create().WithPath($"{Constants.AgentToAgent.AgentPathPrefix}/{PeerIntent}").UsingPost())
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
    /// Builds the directory over a configuration declaring one consultable partner at
    /// <paramref name="peerAddress"/>.
    /// </summary>
    /// <remarks>
    /// The two prompt services are inert here: they describe agents of <em>this</em> installation and
    /// nothing on the path to a colleague published elsewhere reads them.
    /// </remarks>
    /// <param name="peerAddress">Where the partner answers.</param>
    private static ConfigurationAgentDirectoryService BuildDirectory(string peerAddress)
    {
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

        return new ConfigurationAgentDirectoryService(
            new UnreadAgentConfiguration(),
            new UnreadPromptResolver(),
            configuration,
            new FixedHostAddress(),
            new PeerRingKeyService(),
            NullLogger.Instance);
    }

    /// <summary>Resolves the colleague as an agent of this installation would, or hands back nothing.</summary>
    /// <param name="directory">Directory under test.</param>
    private static async Task<AIAgent?> ResolveAsync(ConfigurationAgentDirectoryService directory)
        => (await directory.ResolvePeerAgentAsync(new Records.PeerReference(PeerIntent, PartnerName), CallerIntent))?.Agent;

    /// <summary>
    /// Resolves the colleague and puts a question to it, so the peer records the request this
    /// installation actually sends.
    /// </summary>
    /// <remarks>
    /// The answer is discarded whatever it turns out to be: the stub answers no envelope this side
    /// could read, and every assertion in this group is on what arrived at the peer.
    /// </remarks>
    /// <param name="directory">Directory under test.</param>
    private static async Task ConsultAsync(ConfigurationAgentDirectoryService directory)
    {
        AIAgent colleague = await ResolveAsync(directory)
            ?? throw new InvalidOperationException("The colleague was not resolved, so nothing was ever sent to its peer.");

        AgentSession session = colleague is A2AAgent a2aColleague
            ? await a2aColleague.CreateSessionAsync("conversation-under-test")
            : await colleague.CreateSessionAsync();

        try
        {
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
        string authorization = Assert.Single(headers["Authorization"]);

        Assert.StartsWith("Bearer ", authorization, StringComparison.Ordinal);

        return new JsonWebToken(authorization["Bearer ".Length..]);
    }

    /// <summary>What a served card demands of its callers.</summary>
    /// <param name="Schemes">Schemes the card defines.</param>
    /// <param name="RequiredSchemeName">The one it names in its requirement.</param>
    private sealed record AgentCardSecurity(Dictionary<string, SecurityScheme> Schemes, string RequiredSchemeName);

    /// <summary>Stands in for the domain configuration, which describes agents of this installation only.</summary>
    private sealed class UnreadAgentConfiguration : IAgentConfigurationService
    {
        public Task<List<Records.IntentDefinition>> GetIntentsAsync() => Task.FromResult(new List<Records.IntentDefinition>());

        public Task<List<Records.Prompt>> GetAgentPromptsAsync() => Task.FromResult(new List<Records.Prompt>());
    }

    /// <summary>Stands in for prompt resolution, which no path toward a colleague elsewhere reaches.</summary>
    private sealed class UnreadPromptResolver : IPromptResolverService
    {
        public Task<Records.Prompt[]> GetAllPromptsAsync() => Task.FromResult(Array.Empty<Records.Prompt>());

        public Task<Records.Prompt> ResolveAsync(string promptID)
            => throw new InvalidOperationException($"Prompt '{promptID}' was resolved while consulting a colleague published elsewhere.");
    }

    /// <summary>Reports an address for this installation, which only a colleague of its own ring is reached at.</summary>
    private sealed class FixedHostAddress : IHostAddressService
    {
        public string? ResolveBaseAddress() => "http://127.0.0.1:1";
    }
}
