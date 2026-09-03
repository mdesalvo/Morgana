using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Checks the agent card a published agent serves: that it is reachable without credentials, that
/// it states how to obtain them, and that the endpoint it points at demands them.
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

        // The requirement names a scheme, and the scheme must be one the card also defines: a
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

        // Not required, and that is itself the contract: a consumer that has never heard of this
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

    /// <summary>Well-known address of one published agent's card on the host under test.</summary>
    /// <param name="intent">Intent whose card is being fetched.</param>
    private string CardAddress(string intent)
        => $"{fixture.BaseAddress}/a2a/{intent}/.well-known/agent-card.json";
}
