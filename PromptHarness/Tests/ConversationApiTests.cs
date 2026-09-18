using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Morgana.AI;
using Morgana.AI.Services;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Checks the conversation API a channel talks to — who it lets in, what a start must announce, how
/// it answers for a conversation it does not hold — as status codes and response shapes.
/// </summary>
/// <remarks>
/// No turn is ever run, so no agent and no judge is reached. A conversation these tests need on record
/// is synthesised straight into the run's storage (<see cref="SeedConversationOnRecordAsync"/>), so the
/// only model call of the group is the presentation of the single real start, which the presenter
/// computes once per channel name for the whole process. Every path, status and field is spelled out
/// literally, as in <see cref="AgentCardTests"/>: what must be noticed is the API changing shape.
/// </remarks>
public sealed class ConversationApiTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public ConversationApiTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>
    /// Every endpoint a channel calls, each with the body it would carry, so the refusals below are
    /// asserted on the whole surface rather than on the one endpoint somebody remembered.
    /// </summary>
    public static TheoryData<string, string> ChannelEndpoints => new()
    {
        { "POST", "/api/morgana/conversation/start" },
        { "POST", "/api/morgana/conversation/{id}/message" },
        { "POST", "/api/morgana/conversation/{id}/resume" },
        { "GET", "/api/morgana/conversation/{id}/history" },
        { "POST", "/api/morgana/conversation/{id}/end" }
    };

    [Theory]
    [MemberData(nameof(ChannelEndpoints))]
    public async Task Endpoint_refuses_a_call_without_credentials(string method, string path)
    {
        HttpResponseMessage response = await SendAsync(method, path, NewConversationId(), token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ChannelEndpoints))]
    public async Task Endpoint_refuses_a_token_signed_with_another_key(string method, string path)
    {
        // The harness issuer, the right audience, a current token: only the key is wrong. A channel's
        // name is not its credential, so claiming it proves nothing without the key it was filed under.
        string forgedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        HttpResponseMessage response = await SendAsync(method, path, NewConversationId(), MintToken(HarnessChannel.IssuerName, forgedKey));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ChannelEndpoints))]
    public async Task Endpoint_refuses_a_partners_own_credentials(string method, string path)
    {
        // The mirror of AgentCardTests' channel refused at the A2A door: a partner's token is valid and
        // still not a channel's. A partner carries agent work, never people, so it opens no conversation.
        HttpResponseMessage response = await SendAsync(
            method, path, NewConversationId(), MintToken(MorganaHostFixture.ScopedPartnerName, fixture.ScopedPartnerKey));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Handshakes the start gate must refuse: each one is missing a fact without which no reply could
    /// be delivered or adapted, so the conversation is never opened rather than failing on its first send.
    /// </summary>
    public static TheoryData<string, string> RefusedHandshakes => new()
    {
        { "no channel metadata", """{"conversationId":"{id}"}""" },
        { "no channel name", """{"conversationId":"{id}","channelMetadata":{"coordinates":{"channelName":"","deliveryMode":"webhook","callbackUrl":"http://127.0.0.1:1/hook"},"capabilities":{"supportsRichCards":true,"supportsQuickReplies":true,"supportsStreaming":true,"supportsMarkdown":true}}}""" },
        { "a delivery mode nothing serves", """{"conversationId":"{id}","channelMetadata":{"coordinates":{"channelName":"harness","deliveryMode":"carrier-pigeon"},"capabilities":{"supportsRichCards":true,"supportsQuickReplies":true,"supportsStreaming":true,"supportsMarkdown":true}}}""" },
        { "a webhook with no callback", """{"conversationId":"{id}","channelMetadata":{"coordinates":{"channelName":"harness","deliveryMode":"webhook"},"capabilities":{"supportsRichCards":true,"supportsQuickReplies":true,"supportsStreaming":true,"supportsMarkdown":true}}}""" },
        { "a webhook with a relative callback", """{"conversationId":"{id}","channelMetadata":{"coordinates":{"channelName":"harness","deliveryMode":"webhook","callbackUrl":"/hook"},"capabilities":{"supportsRichCards":true,"supportsQuickReplies":true,"supportsStreaming":true,"supportsMarkdown":true}}}""" },
        { "no capabilities", """{"conversationId":"{id}","channelMetadata":{"coordinates":{"channelName":"harness","deliveryMode":"webhook","callbackUrl":"http://127.0.0.1:1/hook"}}}""" }
    };

    [Theory]
    [MemberData(nameof(RefusedHandshakes))]
    public async Task Start_refuses_an_incomplete_handshake(string missing, string body)
    {
        string conversationId = NewConversationId();

        HttpResponseMessage response = await PostJsonAsync("/api/morgana/conversation/start", body.Replace("{id}", conversationId));

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"A start with {missing} was answered {(int)response.StatusCode}, not 400.");
        Assert.False(ConversationIsOnRecord(conversationId), $"A start refused for {missing} left a conversation on record.");
    }

    /// <summary>
    /// Endpoints that serve a conversation already started, never one they are the first to hear of.
    /// </summary>
    public static TheoryData<string, string> ConversationEndpoints => new()
    {
        { "POST", "/api/morgana/conversation/{id}/message" },
        { "POST", "/api/morgana/conversation/{id}/resume" },
        { "GET", "/api/morgana/conversation/{id}/history" }
    };

    [Theory]
    [MemberData(nameof(ConversationEndpoints))]
    public async Task Endpoint_answers_404_for_a_conversation_never_started(string method, string path)
    {
        string conversationId = NewConversationId();

        HttpResponseMessage response = await SendAsync(method, path, conversationId, MintToken(HarnessChannel.IssuerName, fixture.IssuerKey));

        // Answered as unknown and left unknown: a request that brought a conversation's database into
        // being would make the next one find a conversation with no channel to reply on.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(ConversationIsOnRecord(conversationId), $"{method} {path} left a record behind for a conversation never started.");
    }

    [Fact]
    public async Task Start_answers_once_the_conversation_is_on_record()
    {
        string conversationId = NewConversationId();

        HttpResponseMessage start = await StartAsync(conversationId);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

        // Asked the instant start returns, with nothing awaited in between: a channel may send its
        // first message just as fast and it must find a conversation there, not a 404.
        HttpResponseMessage resume = await SendAsync("POST", "/api/morgana/conversation/{id}/resume", conversationId,
            MintToken(HarnessChannel.IssuerName, fixture.IssuerKey));

        Assert.Equal(HttpStatusCode.Accepted, resume.StatusCode);
        Assert.True(ConversationIsOnRecord(conversationId), "Start answered before the conversation was on record.");
    }

    [Fact]
    public async Task Resume_reports_the_state_to_redraw()
    {
        string conversationId = NewConversationId();
        await SeedConversationOnRecordAsync(conversationId, activeAgent: "billing");

        HttpResponseMessage response = await SendAsync("POST", "/api/morgana/conversation/{id}/resume", conversationId,
            MintToken(HarnessChannel.IssuerName, fixture.IssuerKey));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // What a returning client redraws from: the id it resumed, the agent the conversation was left
        // talking to and the dust gauge (none: the harness runs with dust limiting off).
        Assert.Equal(conversationId, body.GetProperty("conversationId").GetString());
        Assert.True(body.GetProperty("resumed").GetBoolean());
        Assert.Equal("billing", body.GetProperty("activeAgent").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("dustLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("dustExhaustedMessage").ValueKind);
    }

    [Fact]
    public async Task End_can_be_called_twice()
    {
        string conversationId = NewConversationId();
        await SeedConversationOnRecordAsync(conversationId, activeAgent: null);

        string token = MintToken(HarnessChannel.IssuerName, fixture.IssuerKey);

        // A channel ends a conversation on its way out, often more than once (a page closing, a
        // process stopping): the second call finds nothing to tear down and says so without failing.
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("POST", "/api/morgana/conversation/{id}/end", conversationId, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync("POST", "/api/morgana/conversation/{id}/end", conversationId, token)).StatusCode);

        // Ending frees the actors and keeps the record: the conversation can still be resumed.
        Assert.True(ConversationIsOnRecord(conversationId), "Ending a conversation deleted its record.");
    }

    /// <summary>
    /// Starts a conversation with a well-formed handshake as the harness channel. The callback points
    /// nowhere on purpose: nothing here reads the presentation, whose delivery simply fails.
    /// </summary>
    private Task<HttpResponseMessage> StartAsync(string conversationId) =>
        PostJsonAsync("/api/morgana/conversation/start",
            """{"conversationId":"{id}","channelMetadata":{"coordinates":{"channelName":"harness","deliveryMode":"webhook","callbackUrl":"http://127.0.0.1:1/hook"},"capabilities":{"supportsRichCards":true,"supportsQuickReplies":true,"supportsStreaming":true,"supportsMarkdown":true}}}"""
                .Replace("{id}", conversationId));

    /// <summary>
    /// Puts a conversation on record without starting it, as a previous process would have left it:
    /// the handshake written by the host's own persistence service, so schema and encoding are the
    /// ones production writes, plus, when asked, the agent the conversation was left talking to.
    /// </summary>
    /// <param name="activeAgent">Intent of the agent left mid-exchange, or null for none.</param>
    private async Task SeedConversationOnRecordAsync(string conversationId, string? activeAgent)
    {
        SQLiteConversationPersistenceService persistenceService = new SQLiteConversationPersistenceService(
            Microsoft.Extensions.Options.Options.Create(new Records.ConversationPersistenceOptions
            {
                StoragePath = fixture.StoragePath,
                EncryptionKey = fixture.Configuration["Morgana:ConversationPersistence:EncryptionKey"]!
            }),
            NullLogger.Instance);

        await persistenceService.SaveChannelMetadataAsync(conversationId, new ChannelMetadata
        {
            Coordinates = new ChannelCoordinates { ChannelName = "harness", DeliveryMode = "webhook", CallbackUrl = "http://127.0.0.1:1/hook" },
            Capabilities = new ChannelCapabilities(
                SupportsRichCards: true, SupportsQuickReplies: true, SupportsStreaming: true, SupportsMarkdown: true, MaxMessageLength: null)
        });

        if (activeAgent is null)
            return;

        // Only the row's standing is read on resume, never its session, so the session is left empty
        await using SqliteConnection connection = new SqliteConnection(
            $"Data Source={Path.Combine(fixture.StoragePath, $"morgana-{conversationId}.db")}");
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO morgana (agent_identifier, agent_name, agent_session, creation_date, last_update, is_active) " +
            "VALUES (@identifier, @name, zeroblob(0), @now, @now, 1);";
        command.Parameters.AddWithValue("@identifier", $"{activeAgent}-{conversationId}");
        command.Parameters.AddWithValue("@name", activeAgent);
        command.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Posts a raw JSON body as the harness channel, so a malformed handshake reaches the gate as written.</summary>
    private async Task<HttpResponseMessage> PostJsonAsync(string path, string json)
    {
        using HttpClient httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintToken(HarnessChannel.IssuerName, fixture.IssuerKey));

        return await httpClient.PostAsync($"{fixture.BaseAddress}{path}", new StringContent(json, Encoding.UTF8, "application/json"));
    }

    /// <summary>
    /// Calls one endpoint for a conversation, with the body a channel would send there. A null token
    /// sends no Authorization header at all.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(string method, string path, string conversationId, string? token)
    {
        using HttpClient httpClient = new HttpClient();
        if (token is not null)
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        string address = $"{fixture.BaseAddress}{path.Replace("{id}", conversationId)}";
        if (method == "GET")
            return await httpClient.GetAsync(address);

        // Start and message carry a body; the other endpoints take none
        object? body = path.EndsWith("/start")
            ? new { conversationId }
            : path.EndsWith("/message") ? new { conversationId, text = "Hello" } : null;

        return body is null
            ? await httpClient.PostAsync(address, content: null)
            : await httpClient.PostAsJsonAsync(address, body);
    }

    /// <summary>Mints a five-minute token the way any channel mints one, signed under the given issuer and key.</summary>
    private string MintToken(string issuer, string symmetricKey) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = fixture.Configuration["Morgana:Authentication:Audience"],
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, issuer)]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)), SecurityAlgorithms.HmacSha256)
        });

    /// <summary>Whether the host keeps a record of the conversation: its own database in the run's storage path.</summary>
    private bool ConversationIsOnRecord(string conversationId) =>
        File.Exists(Path.Combine(fixture.StoragePath, $"morgana-{conversationId}.db"));

    /// <summary>A conversation id no other test has used, in the form channels mint them.</summary>
    private static string NewConversationId() => Guid.NewGuid().ToString("N");
}
