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
/// Checks the card a published agent serves, open and stating how to authenticate, together with the gate behind it and how far a credential reaches.
/// A card is a wire contract, so it is asserted deterministically on the JSON a foreign implementation reads, every name spelled out literally.
/// </summary>
public sealed class AgentCardTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>Receives the assembly's one live host.</summary>
    public AgentCardTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>
    /// Every agent of the example domain, because the ring is raised whole: an installation publishes
    /// what it can answer, so an intent nobody here consults still serves a card.
    /// </summary>
    public static TheoryData<string> PublishedIntents => ["billing", "contract", "gardening", "inventory"];

    [Theory]
    [MemberData(nameof(PublishedIntents))]
    public async Task Card_is_served_without_credentials(string intent)
    {
        // A client of its own, carrying nothing the harness channel would add.
        using HttpClient httpClient = new HttpClient();

        // No Authorization header, deliberately: a caller that has to authenticate to learn how to
        // authenticate can never begin, so this endpoint staying open is the contract and not an
        // oversight. Fetched the way A2ACardResolver fetches it.
        HttpResponseMessage response = await httpClient.GetAsync(CardAddress(intent), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The card names the agent and says what it is for, which is all a stranger has to choose it by.
        JsonElement card = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("name").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(card.GetProperty("description").GetString()));

        // The transport half of the contract: a caller binds to what the card advertises, so an
        // address that is relative, empty or missing leaves it with nowhere to send its question.
        // An installation learns where it answers only once its server binds, after the card was written: a card
        // frozen before that would cost every peer its colleague and say nothing else, as would one served from a copy.
        JsonElement supportedInterface = Assert.Single(card.GetProperty("supportedInterfaces").EnumerateArray().ToArray());

        Assert.True(Uri.TryCreate(supportedInterface.GetProperty("url").GetString(), UriKind.Absolute, out _),
            "The card advertises no absolute URL to reach the agent at.");
    }

    [Theory]
    [MemberData(nameof(PublishedIntents))]
    public async Task Card_declares_how_a_caller_authenticates(string intent)
    {
        // The card as a stranger fetches it, without credentials.
        using HttpClient httpClient = new HttpClient();
        JsonElement card = await httpClient.GetFromJsonAsync<JsonElement>(CardAddress(intent), TestContext.Current.CancellationToken);

        // The requirement names a scheme and the scheme must be one the card also defines: a
        // requirement pointing at nothing tells a caller it must authenticate and not how.
        JsonElement requirement = Assert.Single(card.GetProperty("securityRequirements").EnumerateArray().ToArray());
        JsonProperty requiredScheme = Assert.Single(requirement.GetProperty("schemes").EnumerateObject().ToArray());

        // The scheme the requirement names is a JWT bearer, the one credential the gate behind the card proves.
        JsonElement declaredScheme = card.GetProperty("securitySchemes").GetProperty(requiredScheme.Name);
        Assert.Equal("bearer", declaredScheme.GetProperty("httpAuthSecurityScheme").GetProperty("scheme").GetString());
        Assert.Equal("JWT", declaredScheme.GetProperty("httpAuthSecurityScheme").GetProperty("bearerFormat").GetString());

        // The card declares the scheme and stops, which is the contract: an issuer is the name this host filed one
        // caller under and an audience is agreed with the key, so both travel out of band and a capability extension
        // stating them would be a regression.
        Assert.False(card.GetProperty("capabilities").TryGetProperty("extensions", out JsonElement extensions)
                     && extensions.ValueKind is JsonValueKind.Array
                     && extensions.GetArrayLength() > 0,
            "The card advertises a capability extension: neither claim value belongs on a document served to everyone.");
    }

    [Theory]
    [MemberData(nameof(PublishedIntents))]
    public async Task Agent_endpoint_refuses_a_call_the_card_was_not_read_for(string intent)
    {
        // A client of its own, carrying no token at all.
        using HttpClient httpClient = new HttpClient();

        // The other half of the pair above: the card is open precisely because what it points at is
        // not. A request carrying no token is turned away before it can reach an agent, so an open
        // discovery document never widens the surface it describes.
        HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            $"{fixture.BaseAddress}/a2a/{intent}",
            new { jsonrpc = "2.0", id = "1", method = "message/send" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Agent_endpoint_refuses_a_channels_own_credentials()
    {
        // A caller is a channel or a colleague, never both: the harness's channel token is entirely valid and is refused
        // because it is presented at a door it was not cut for. Behind this door a request reaches an agent's actor with
        // none of the user's guard, the classifier and the gates the conversation API applies.
        HttpResponseMessage response = await CallAgentAsync(
            MorganaHostFixture.ScopedPartnerAgent, HarnessChannel.IssuerName, fixture.IssuerKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Every published agent this run's partner is not admitted to: the theory data above minus <see cref="MorganaHostFixture.ScopedPartnerAgent"/>.
    /// They are spelled out for the same reason as everything here, so the published surface changing shape is noticed.
    /// </summary>
    [Theory]
    [InlineData("billing")]
    [InlineData("contract")]
    [InlineData("gardening")]
    public async Task Agent_endpoint_refuses_a_partner_not_admitted_to_it(string closedAgent)
    {
        // Proven to be a colleague and still turned away: this run admits its partner to one agent and every other one
        // answers it as it answers a stranger, which lets an installation open to a supplier one agent at a time.
        HttpResponseMessage response = await CallAgentAsync(
            closedAgent, MorganaHostFixture.ScopedPartnerName, fixture.ScopedPartnerKey);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Agent_endpoint_admits_a_partner_scoped_to_it()
    {
        // The other half, which keeps the two above from passing for the wrong reason, since a gate refusing everything
        // would satisfy both. Only the gate is under test, so the assertion stops at not turned away: what the A2A handler
        // makes of a deliberately incomplete JSON-RPC body is not measured here.
        HttpResponseMessage response = await CallAgentAsync(
            MorganaHostFixture.ScopedPartnerAgent, MorganaHostFixture.ScopedPartnerName, fixture.ScopedPartnerKey);

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Calls one published agent's JSON-RPC endpoint with a token minted here, the way any A2A consumer holding a key would mint one.</summary>
    /// <param name="intent">Published agent to call.</param>
    /// <param name="issuer">Issuer to sign under, which is what the gate reads.</param>
    /// <param name="symmetricKey">Key that issuer is declared with on the host under test.</param>
    private async Task<HttpResponseMessage> CallAgentAsync(string intent, string issuer, string symmetricKey)
    {
        // A token valid in every respect the gate checks, so what decides is only which issuer signed it.
        string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = fixture.Configuration["Morgana:Authentication:Audience"],
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, issuer)]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)), SecurityAlgorithms.HmacSha256)
        });

        // The token as an A2A client sends it.
        using HttpClient httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // A minimal JSON-RPC call: the gate answers before the handler ever reads its body.
        return await httpClient.PostAsJsonAsync(
            $"{fixture.BaseAddress}/a2a/{intent}",
            new { jsonrpc = "2.0", id = "1", method = "message/send" },
            TestContext.Current.CancellationToken);
    }

    /// <summary>Well-known address of one published agent's card on the host under test.</summary>
    /// <param name="intent">Intent whose card is being fetched.</param>
    private string CardAddress(string intent)
        => $"{fixture.BaseAddress}/a2a/{intent}/.well-known/agent-card.json";
}