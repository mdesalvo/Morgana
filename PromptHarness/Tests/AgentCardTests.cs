using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Checks the agent card a published agent serves — that it is reachable without credentials, that
/// it states how to obtain them — and the gate behind it: which credentials that endpoint demands,
/// and how far the ones it accepts actually reach.
/// </summary>
/// <remarks>
/// The only group here that costs nothing and grades nothing: a card is a wire contract, not prose,
/// so it is asserted deterministically on the JSON a foreign implementation would actually read.
/// Everything it names is spelled out literally rather than referenced from the framework — a test
/// comparing <c>Constants</c> against itself asserts that a constant equals a constant, while the
/// point here is that the published document did not silently change shape under whoever consumes it.
/// </remarks>
public sealed class AgentCardTests
{
    /// <summary>URI of the extension by which a card declares how a caller mints its bearer token.</summary>
    private const string BearerIssuanceExtensionUri = "https://mdesalvo.github.io/Morgana/a2a/extensions/bearer-issuance/v1";

    /// <summary>Issuer the host expects peer traffic to be signed under, as its cards must declare it.</summary>
    private const string PeerIssuerName = "morgana";

    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public AgentCardTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>
    /// Every agent of the example domain, because the ring is raised whole: an installation publishes
    /// what it can answer, so an intent nobody here consults still serves a card.
    /// </summary>
    public static TheoryData<string> PublishedIntents => ["billing", "contract", "inventory", "monkeys"];

    [Theory]
    [MemberData(nameof(PublishedIntents))]
    public async Task Card_is_served_without_credentials(string intent)
    {
        using HttpClient httpClient = new HttpClient();

        // No Authorization header, deliberately: a caller that has to authenticate to learn how to
        // authenticate can never begin, so this endpoint staying open is the contract and not an
        // oversight. Fetched the way A2ACardResolver fetches it.
        HttpResponseMessage response = await httpClient.GetAsync(CardAddress(intent));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonElement card = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("description").GetString()));

        // The transport half of the contract: a caller binds to what the card advertises, so an
        // address that is relative, empty or missing leaves it with nowhere to send its question.
        // It is also the only thing watching the seam underneath: an installation does not know
        // where it answers until its server binds, so the card learns that after it was written.
        // Whatever stops carrying it here — a card frozen before the address was known, a hosting
        // layer that answers from a copy — costs every peer its colleague and says nothing else.
        JsonElement supportedInterface = Assert.Single(card.GetProperty("supportedInterfaces").EnumerateArray().ToArray());

        Assert.True(Uri.TryCreate(supportedInterface.GetProperty("url").GetString(), UriKind.Absolute, out _),
            "The card advertises no absolute URL to reach the agent at.");
    }

    [Theory]
    [MemberData(nameof(PublishedIntents))]
    public async Task Card_declares_how_a_caller_authenticates(string intent)
    {
        using HttpClient httpClient = new HttpClient();

        JsonElement card = await httpClient.GetFromJsonAsync<JsonElement>(CardAddress(intent));

        // The requirement names a scheme and the scheme must be one the card also defines: a
        // requirement pointing at nothing tells a caller it must authenticate and not how.
        JsonElement requirement = Assert.Single(card.GetProperty("securityRequirements").EnumerateArray().ToArray());
        JsonProperty requiredScheme = Assert.Single(requirement.GetProperty("schemes").EnumerateObject().ToArray());

        JsonElement declaredScheme = card.GetProperty("securitySchemes").GetProperty(requiredScheme.Name);

        Assert.Equal("bearer", declaredScheme.GetProperty("httpAuthSecurityScheme").GetProperty("scheme").GetString());
        Assert.Equal("JWT", declaredScheme.GetProperty("httpAuthSecurityScheme").GetProperty("bearerFormat").GetString());

        // What the standard has no field for: which claims the token must carry. Absent it, a caller
        // holding the shared secret still cannot produce a token this host will accept.
        JsonElement bearerIssuance = Assert.Single(card.GetProperty("capabilities").GetProperty("extensions").EnumerateArray()
            .Where(extension => extension.GetProperty("uri").GetString() == BearerIssuanceExtensionUri).ToArray());

        JsonElement issuanceParameters = bearerIssuance.GetProperty("params");

        Assert.Equal(PeerIssuerName, issuanceParameters.GetProperty("issuer").GetString());
        Assert.Equal(
            fixture.Configuration["Morgana:Authentication:Audience"],
            issuanceParameters.GetProperty("audience").GetString());

        // Not required and that is itself the contract: a consumer that has never heard of this
        // extension must still be able to use the card, held to the standard requirement above.
        Assert.False(bearerIssuance.GetProperty("required").GetBoolean());
    }

    [Theory]
    [MemberData(nameof(PublishedIntents))]
    public async Task Agent_endpoint_refuses_a_call_the_card_was_not_read_for(string intent)
    {
        using HttpClient httpClient = new HttpClient();

        // The other half of the pair above: the card is open precisely because what it points at is
        // not. A request carrying no token is turned away before it can reach an agent, so an open
        // discovery document never widens the surface it describes.
        HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            $"{fixture.BaseAddress}/a2a/{intent}",
            new { jsonrpc = "2.0", id = "1", method = "message/send" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Agent_endpoint_refuses_a_channels_own_credentials()
    {
        // A caller is a channel or a colleague, never both. The harness authenticates as a channel
        // and its token is entirely valid — signed with a key this host declared, current, addressed
        // to the right audience — which is the whole point: what is refused here is not a bad
        // credential but a good one presented at a door it was not cut for. Behind this one a request
        // reaches an agent's actor with none of the guard, classifier, rate limit and dust budget the
        // conversation API applies.
        HttpResponseMessage response = await CallAgentAsync(
            MorganaHostFixture.ScopedSystemAgent, HarnessChannel.IssuerName, fixture.IssuerKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Every published agent this run's scoped system is NOT admitted to — the theory data above
    /// minus <see cref="MorganaHostFixture.ScopedSystemAgent"/>, spelled out for the same reason
    /// everything else here is: what must be noticed is the published surface changing shape.
    /// </summary>
    [Theory]
    [InlineData("billing")]
    [InlineData("contract")]
    [InlineData("monkeys")]
    public async Task Agent_endpoint_refuses_a_system_not_admitted_to_it(string closedAgent)
    {
        // Proven to be a colleague and still turned away: this run declares its scoped system as
        // admitted to one desk and every other desk of the same installation answers it exactly as
        // it answers a stranger. That is what lets an installation be opened to a customer, a
        // supplier or a marketplace one agent at a time, rather than whole or not at all.
        HttpResponseMessage response = await CallAgentAsync(
            closedAgent, MorganaHostFixture.ScopedSystemIssuerName, fixture.ScopedSystemKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Agent_endpoint_admits_a_system_scoped_to_it()
    {
        // The other half and the one that keeps the two above from passing for the wrong reason: a
        // gate refusing everything would satisfy them both. Only the gate is under test here, so the
        // assertion stops at "not turned away" — what the A2A handler makes of a deliberately
        // incomplete JSON-RPC body is its business and asserting it would tie this test to a
        // protocol shape it is not measuring.
        HttpResponseMessage response = await CallAgentAsync(
            MorganaHostFixture.ScopedSystemAgent, MorganaHostFixture.ScopedSystemIssuerName, fixture.ScopedSystemKey);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Calls one published agent's JSON-RPC endpoint with a token minted here, the way any A2A
    /// consumer holding a key would mint one.
    /// </summary>
    /// <param name="intent">Published agent to call.</param>
    /// <param name="issuer">Issuer to sign under, which is what the gate reads.</param>
    /// <param name="symmetricKey">Key that issuer is declared with on the host under test.</param>
    private async Task<HttpResponseMessage> CallAgentAsync(string intent, string issuer, string symmetricKey)
    {
        string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = fixture.Configuration["Morgana:Authentication:Audience"],
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, issuer)]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)), SecurityAlgorithms.HmacSha256)
        });

        using HttpClient httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        return await httpClient.PostAsJsonAsync(
            $"{fixture.BaseAddress}/a2a/{intent}",
            new { jsonrpc = "2.0", id = "1", method = "message/send" });
    }

    /// <summary>Well-known address of one published agent's card on the host under test.</summary>
    /// <param name="intent">Intent whose card is being fetched.</param>
    private string CardAddress(string intent)
        => $"{fixture.BaseAddress}/a2a/{intent}/.well-known/agent-card.json";
}
