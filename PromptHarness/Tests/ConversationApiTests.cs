using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Checks the REST API a channel talks to — who it lets in, what a start must announce, how it answers
/// for a conversation it does not hold, which command requests it admits — as status codes and shapes.
/// </summary>
/// <remarks>
/// No turn is ever run, so no agent and no judge is reached. A conversation these tests need on record
/// is synthesised straight into the run's storage (<see cref="ChannelApiClient.SeedConversationOnRecordAsync"/>), so the
/// only model call of the group is the presentation of the single real start, which the presenter
/// computes once per channel name for the whole process. Every path, status and field is spelled out
/// literally, as in <see cref="AgentCardTests"/>: what must be noticed is the API changing shape.
/// </remarks>
public sealed class ConversationApiTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>The API as a channel calls it, on that host.</summary>
    private readonly ChannelApiClient api;

    public ConversationApiTests(MorganaHostFixture fixture)
    {
        this.fixture = fixture;
        api = new ChannelApiClient(fixture);
    }

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
        { "POST", "/api/morgana/conversation/{id}/end" },
        { "GET", "/api/morgana/commands" },
        { "POST", "/api/morgana/conversation/{id}/command" }
    };

    [Theory]
    [MemberData(nameof(ChannelEndpoints))]
    public async Task Endpoint_refuses_a_call_without_credentials(string method, string path)
    {
        HttpResponseMessage response = await api.SendAsync(method, path, ChannelApiClient.NewConversationId(), token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ChannelEndpoints))]
    public async Task Endpoint_refuses_a_token_signed_with_another_key(string method, string path)
    {
        // The harness issuer, the right audience, a current token: only the key is wrong. A channel's
        // name is not its credential, so claiming it proves nothing without the key it was filed under.
        string forgedKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        HttpResponseMessage response = await api.SendAsync(method, path, ChannelApiClient.NewConversationId(), api.MintToken(HarnessChannel.IssuerName, forgedKey));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ChannelEndpoints))]
    public async Task Endpoint_refuses_a_partners_own_credentials(string method, string path)
    {
        // The mirror of AgentCardTests' channel refused at the A2A door: a partner's token is valid and
        // still not a channel's. A partner carries agent work, never people, so it opens no conversation.
        HttpResponseMessage response = await api.SendAsync(
            method, path, ChannelApiClient.NewConversationId(), api.MintToken(MorganaHostFixture.ScopedPartnerName, fixture.ScopedPartnerKey));

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
        string conversationId = ChannelApiClient.NewConversationId();

        HttpResponseMessage response = await api.PostJsonAsync("/api/morgana/conversation/start", body.Replace("{id}", conversationId));

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"A start with {missing} was answered {(int)response.StatusCode}, not 400.");
        Assert.False(api.ConversationIsOnRecord(conversationId), $"A start refused for {missing} left a conversation on record.");
    }

    /// <summary>
    /// Endpoints that serve a conversation already started, never one they are the first to hear of.
    /// </summary>
    public static TheoryData<string, string> ConversationEndpoints => new()
    {
        { "POST", "/api/morgana/conversation/{id}/message" },
        { "POST", "/api/morgana/conversation/{id}/resume" },
        { "GET", "/api/morgana/conversation/{id}/history" },
        { "POST", "/api/morgana/conversation/{id}/command" }
    };

    [Theory]
    [MemberData(nameof(ConversationEndpoints))]
    public async Task Endpoint_answers_404_for_a_conversation_never_started(string method, string path)
    {
        string conversationId = ChannelApiClient.NewConversationId();

        HttpResponseMessage response = await api.SendAsync(method, path, conversationId, api.HarnessToken());

        // Answered as unknown and left unknown: a request that brought a conversation's database into
        // being would make the next one find a conversation with no channel to reply on.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(api.ConversationIsOnRecord(conversationId), $"{method} {path} left a record behind for a conversation never started.");
    }

    [Fact]
    public async Task Start_answers_once_the_conversation_is_on_record()
    {
        string conversationId = ChannelApiClient.NewConversationId();

        HttpResponseMessage start = await api.StartAsync(conversationId);
        Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

        // Asked the instant start returns, with nothing awaited in between: a channel may send its
        // first message just as fast and it must find a conversation there, not a 404.
        HttpResponseMessage resume = await api.SendAsync("POST", "/api/morgana/conversation/{id}/resume", conversationId,
            api.HarnessToken());

        Assert.Equal(HttpStatusCode.Accepted, resume.StatusCode);
        Assert.True(api.ConversationIsOnRecord(conversationId), "Start answered before the conversation was on record.");
    }

    [Fact]
    public async Task Resume_reports_the_state_to_redraw()
    {
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent: "billing");

        HttpResponseMessage response = await api.SendAsync("POST", "/api/morgana/conversation/{id}/resume", conversationId,
            api.HarnessToken());
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
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent: null);

        string token = api.HarnessToken();

        // A channel ends a conversation on its way out, often more than once (a page closing, a
        // process stopping): the second call finds nothing to tear down and says so without failing.
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync("POST", "/api/morgana/conversation/{id}/end", conversationId, token)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync("POST", "/api/morgana/conversation/{id}/end", conversationId, token)).StatusCode);

        // Ending frees the actors and keeps the record: the conversation can still be resumed.
        Assert.True(api.ConversationIsOnRecord(conversationId), "Ending a conversation deleted its record.");
    }

    [Fact]
    public async Task Health_answers_without_credentials()
    {
        // The liveness probe is asked by infrastructure holding no channel key
        HttpResponseMessage response = await api.SendAsync("GET", "/api/morgana/health", ChannelApiClient.NewConversationId(), token: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Command_catalogue_publishes_compact()
    {
        HttpResponseMessage response = await api.SendAsync("GET", "/api/morgana/commands", ChannelApiClient.NewConversationId(), api.HarnessToken());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // The one command the framework itself publishes, as a channel's palette reads it
        JsonElement compact = body.GetProperty("commands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "compact");
        Assert.True(compact.GetProperty("requiresActiveAgent").GetBoolean());
        Assert.False(compact.GetProperty("requiresConfirmation").GetBoolean());
    }

    /// <summary>
    /// Command requests the channel got wrong, each on a conversation that exists: refused before the
    /// command runs, with the name echoed back so the channel can tell which of its calls it was.
    /// </summary>
    public static TheoryData<string, string, string?> RefusedCommandRequests => new()
    {
        { "an unknown name", """{"name":"transmogrify"}""", "billing" },
        { "no agent carrying the conversation", """{"name":"compact"}""", null },
        { "an option the command does not declare", """{"name":"compact","options":{"depth":"3"}}""", "billing" }
    };

    [Theory]
    [MemberData(nameof(RefusedCommandRequests))]
    public async Task Command_refuses_a_request_it_could_not_run(string reason, string body, string? activeAgent)
    {
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent);

        HttpResponseMessage response = await api.SendCommandAsync(conversationId, body.Replace("{id}", conversationId));

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"A command with {reason} was answered {(int)response.StatusCode}, not 400.");
        JsonElement refusal = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(refusal.TryGetProperty("name", out _), $"The refusal of a command with {reason} does not name the command.");
    }

    [Fact]
    public async Task Command_runs_on_the_agent_carrying_the_conversation()
    {
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent: "billing");

        HttpResponseMessage response = await api.SendCommandAsync(conversationId, """{"name":"compact"}""");

        // The command runs once it is admitted and the acknowledgement names it
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(conversationId, body.GetProperty("conversationId").GetString());
        Assert.Equal("compact", body.GetProperty("command").GetString());
    }
}
