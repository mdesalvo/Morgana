using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.Contracts;
using Morgana.Web.Filters;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Checks the REST API a channel talks to, as status codes and shapes: who it lets in, what a start must announce, how it answers for an unknown conversation and which commands it admits.
/// Every test works on a conversation id of its own, seeded straight on the run's storage where it needs one on record, so no turn runs and no test reads another's state.
/// Every path, status and field is spelled out literally, so the API changing shape is noticed.
/// </summary>
public sealed class ConversationApiTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>The API as a channel calls it, on that host.</summary>
    private readonly ChannelApiClient api;

    /// <summary>Receives the assembly's one live host and calls its API as the harness channel.</summary>
    public ConversationApiTests(MorganaHostFixture fixture)
    {
        this.fixture = fixture;
        api = new ChannelApiClient(fixture);
    }

    /// <summary>Every endpoint a channel calls, so the refusals below are asserted on the whole surface rather than on the one endpoint somebody remembered.</summary>
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
        // No Authorization header at all: the gate is fail-closed on every endpoint but the health probe.
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

        // The token is proven against the key the issuer was declared with, never against the one it was signed with.
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
    /// Handshakes the start gate must refuse, each missing a fact without which no reply could be delivered or adapted.
    /// The conversation is never opened rather than failing on its first send.
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

        // The handshake as written, under a fresh id so the record can be checked afterwards.
        HttpResponseMessage response = await api.PostJsonAsync("/api/morgana/conversation/start", body.Replace("{id}", conversationId), TestContext.Current.CancellationToken);

        // Refused with nothing left on record that a later call could mistake for an open conversation.
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
        { "POST", "/api/morgana/conversation/{id}/end" },
        { "POST", "/api/morgana/conversation/{id}/command" }
    };

    [Theory]
    [MemberData(nameof(ConversationEndpoints))]
    public async Task Endpoint_answers_404_for_a_conversation_never_started(string method, string path)
    {
        string conversationId = ChannelApiClient.NewConversationId();

        // A valid channel token on an id nobody started.
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

        // The one real start of the group, whose welcome the presenter caches for the whole process.
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
    public async Task Start_refuses_an_id_already_on_record()
    {
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent: "billing", callbackUrl: "http://127.0.0.1:1/owner");

        // A second start naming the owner's id, with the harness's own handshake.
        HttpResponseMessage response = await api.StartAsync(conversationId);

        // Whoever names a conversation someone else opened is refused and its handshake stays the owner's:
        // accepted, the start would have redirected every later reply to the caller's own callback
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("http://127.0.0.1:1/owner", await api.QueryRecordAsync(conversationId, "SELECT callback_url FROM channel_metadata;"));
        Assert.Equal("billing", await api.HostPersistenceService().GetMostRecentActiveAgentAsync(conversationId));
    }

    [Fact]
    public async Task Record_takes_a_departed_agent_off_active_duty_and_keeps_its_session()
    {
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent: "billing");

        // The agent's session as it was written, to compare byte for byte afterwards.
        object? sessionBefore = await api.QueryRecordAsync(conversationId, "SELECT hex(agent_session) FROM morgana WHERE agent_name = 'billing';");

        // The agent hands the conversation back, which takes it off active duty.
        await api.HostPersistenceService().DeactivateAgentAsync(conversationId, "billing");

        // A second call finds the row already off duty and leaves it as it is
        await api.HostPersistenceService().DeactivateAgentAsync(conversationId, "billing");

        // The record names no agent any more while the session the agent wrote stays byte for byte as it was
        Assert.Null(await api.HostPersistenceService().GetMostRecentActiveAgentAsync(conversationId));
        Assert.Equal(sessionBefore, await api.QueryRecordAsync(conversationId, "SELECT hex(agent_session) FROM morgana WHERE agent_name = 'billing';"));
    }

    [Fact]
    public async Task Resume_reports_the_state_to_redraw()
    {
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent: "billing");

        // A client coming back to a conversation billing was carrying.
        HttpResponseMessage response = await api.SendAsync("POST", "/api/morgana/conversation/{id}/resume", conversationId,
            api.HarnessToken());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // What a returning client redraws from: the id it resumed, the agent the conversation was left
        // talking to and the conversation as every push describes it, whose dust gauge is absent because
        // the harness runs with dust limiting off.
        Assert.Equal(conversationId, body.GetProperty("conversationId").GetString());
        Assert.True(body.GetProperty("resumed").GetBoolean());
        Assert.Equal("billing", body.GetProperty("activeAgent").GetString());
        JsonElement conversationMetadata = body.GetProperty("conversationMetadata");
        Assert.Equal(JsonValueKind.Null, conversationMetadata.GetProperty("dustLevel").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("dustExhaustedMessage").ValueKind);

        // A resumed client may send before any push reaches it, so the wait it owes a turn travels here too
        Assert.True(conversationMetadata.GetProperty("replyTimeoutSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task History_of_a_started_conversation_nobody_spoke_in_is_empty_not_missing()
    {
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent: null);

        // The transcript of a conversation started and never spoken in.
        HttpResponseMessage response = await api.SendAsync("GET", "/api/morgana/conversation/{id}/history", conversationId, api.HarnessToken());

        // Started and silent is a conversation with an empty transcript: only one never started is a 404.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ConversationHistoryResponse? history = await response.Content.ReadFromJsonAsync<ConversationHistoryResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(history);
        Assert.Empty(history.Messages);
    }

    [Fact]
    public async Task End_can_be_called_twice()
    {
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent: null);

        // One token for both calls, as a channel holds one for its whole session.
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
        // The catalogue as a channel reads it to fill its palette.
        HttpResponseMessage response = await api.SendAsync("GET", "/api/morgana/commands", ChannelApiClient.NewConversationId(), api.HarnessToken());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);

        // The one command the framework itself publishes, as a channel's palette reads it
        JsonElement compact = body.GetProperty("commands").EnumerateArray()
            .Single(command => command.GetProperty("name").GetString() == "compact");
        Assert.True(compact.GetProperty("requiresActiveAgent").GetBoolean());
        Assert.False(compact.GetProperty("requiresConfirmation").GetBoolean());
    }

    /// <summary>
    /// Command requests the channel got wrong, each on a conversation that exists, refused before the command runs.
    /// The name is echoed back so the channel can tell which of its calls it was.
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

        // The request as the channel wrote it.
        HttpResponseMessage response = await api.SendCommandAsync(conversationId, body.Replace("{id}", conversationId), TestContext.Current.CancellationToken);

        // A 400 that names the command, whatever the request got wrong.
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"A command with {reason} was answered {(int)response.StatusCode}, not 400.");
        JsonElement refusal = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.True(refusal.TryGetProperty("name", out _), $"The refusal of a command with {reason} does not name the command.");
    }

    [Fact]
    public async Task Command_runs_on_the_agent_carrying_the_conversation()
    {
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent: "billing");

        // /compact on an agent with no history, which answers without reaching a model.
        HttpResponseMessage response = await api.SendCommandAsync(conversationId, """{"name":"compact"}""", TestContext.Current.CancellationToken);

        // The command runs once it is admitted and the acknowledgement names it
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        JsonElement body = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        Assert.Equal(conversationId, body.GetProperty("conversationId").GetString());
        Assert.Equal("compact", body.GetProperty("command").GetString());
    }

    /// <summary>
    /// The run a channel names on its request is the run every frame of it hands back, the outcome included.
    /// That is what lets a channel tell this run's outcome from a late one of an earlier run of the same command.
    /// </summary>
    [Fact]
    public async Task Command_outcome_names_the_run_the_channel_asked_for()
    {
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent: "billing", fixture.Channel.CallbackUrl);

        // A run the channel names, delivered to the harness channel where its frames are read.
        HttpResponseMessage response = await api.SendCommandAsync(conversationId, """{"name":"compact","invocationId":"run-7"}""", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Every frame the run sent is on the channel by the time the call returns
        List<ChannelMessage> frames = [];
        while (frames.LastOrDefault()?.Progress is not { Finished: true })
            frames.Add(await fixture.Channel.ReceiveAsync(conversationId, TimeSpan.FromSeconds(15)));
        Assert.All(frames, frame => Assert.Equal("run-7", frame.Progress?.InvocationId));
    }

    /// <summary>
    /// A command declaring that it cannot be taken back is refused with 400 when the request carries no Yes.
    /// No command Morgana ships asks for one, so the gate is judged alone on a registry holding such a command.
    /// </summary>
    [Fact]
    public async Task Command_needing_a_yes_is_refused_without_one()
    {
        // The request without the confirmation its command declares it needs.
        (ActionExecutingContext context, bool ran) = await AdmitAsync(new ExecuteCommandRequest("wipe"));

        // The gate stopped it with the 400 a channel reads, naming the command and the missing Yes.
        Assert.False(ran);
        BadRequestObjectResult refusal = Assert.IsType<BadRequestObjectResult>(context.Result);
        Assert.Equal(400, refusal.StatusCode);
        JsonElement body = JsonSerializer.SerializeToElement(refusal.Value);
        Assert.Equal("Command requires confirmation", body.GetProperty("error").GetString());
        Assert.Equal("wipe", body.GetProperty("name").GetString());
    }

    /// <summary>The same command carrying the Yes its channel obtained passes the gate untouched.</summary>
    [Fact]
    public async Task Command_needing_a_yes_is_admitted_with_one()
    {
        (ActionExecutingContext context, bool ran) = await AdmitAsync(new ExecuteCommandRequest("wipe", Confirmed: true));

        // The gate let it go on and left no result of its own.
        Assert.True(ran);
        Assert.Null(context.Result);
    }

    /// <summary>
    /// Puts <paramref name="request"/> through the admission gate as the command route would, on a registry holding only <see cref="IrreversibleCommand"/>.
    /// It answers the context the gate left and whether the gate let the request go on.
    /// </summary>
    private async Task<(ActionExecutingContext Context, bool Ran)> AdmitAsync(ExecuteCommandRequest request)
    {
        // The real gate, over a registry whose one command needs a Yes and over the host's record.
        CommandAdmissionFilter gate = new CommandAdmissionFilter(
            new CommandRegistryService([new IrreversibleCommand()]), api.HostPersistenceService(), NullLogger.Instance);

        // The context MVC would build for the command route, the request bound as its action argument.
        RouteData route = new RouteData();
        route.Values["conversationId"] = ChannelApiClient.NewConversationId();
        ActionExecutingContext context = new ActionExecutingContext(
            new ActionContext(new DefaultHttpContext(), route, new ActionDescriptor()),
            [],
            new Dictionary<string, object?> { ["request"] = request },
            controller: new object());

        // The next step only records that the gate reached it, since the command must never run.
        bool ran = false;
        await gate.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, [], context.Controller));
        });

        // The result the gate set, if any, with whether it let the request through.
        return (context, ran);
    }

    /// <summary>A command that cannot be taken back, which is all the gate reads of it: it is never run.</summary>
    private sealed class IrreversibleCommand : ICommand
    {
        /// <inheritdoc />
        public CommandDescriptor Descriptor { get; } = new("wipe", "Forget this conversation", RequiresConfirmation: true);

        /// <inheritdoc />
        public Task ExecuteAsync(string conversationId, string? invocationId, IReadOnlyDictionary<string, string> options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The admission gate never runs a command.");
    }
}