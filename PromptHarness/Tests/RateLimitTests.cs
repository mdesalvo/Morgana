using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The per-conversation rate limit at the REST gate: which calls it counts and how it refuses the one
/// past the window. A message and a command fill the same window.
/// </summary>
/// <remarks>
/// <para><strong>Requires the limit switched on at boot</strong>, process-wide like the dust budget.
/// Run this class on its own:</para>
/// <code>Harness__RateLimitPerMinute=3 dotnet test PromptHarness.csproj --filter "FullyQualifiedName~RateLimitTests"</code>
/// <para>No model is reached: the window is filled with <c>/compact</c> on a conversation seeded with an
/// agent holding no history, which answers without summarizing. Every message sent is one the gate
/// refuses. Every status and field is spelled out literally, as in <see cref="ConversationApiTests"/>.</para>
/// </remarks>
public sealed class RateLimitTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>The API as a channel calls it, on that host.</summary>
    private readonly ChannelApiClient api;

    public RateLimitTests(MorganaHostFixture fixture)
    {
        this.fixture = fixture;
        api = new ChannelApiClient(fixture);
    }

    /// <summary>Calls per minute the host admits; skips the test on a host booted without the limit.</summary>
    private int CallsPerMinute()
    {
        Assert.SkipWhen(fixture.Options.RateLimitPerMinute is null,
            "Rate limiting is off: run with Harness__RateLimitPerMinute set, in this class's own invocation.");
        return fixture.Options.RateLimitPerMinute!.Value;
    }

    [Fact]
    public async Task Command_past_the_window_is_answered_429()
    {
        int callsPerMinute = CallsPerMinute();
        string conversationId = await SeedConversationWithAgentAsync();

        for (int call = 1; call <= callsPerMinute; call++)
            Assert.Equal(HttpStatusCode.Accepted, (await SendCompactAsync(conversationId)).StatusCode);

        HttpResponseMessage refused = await SendCompactAsync(conversationId);

        // The client learns when to come back and which window it hit; the user hears why over the channel
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.True(refused.Headers.Contains("Retry-After"), "A 429 came back with no Retry-After header.");
        JsonElement body = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Rate limit exceeded", body.GetProperty("error").GetString());
        Assert.Equal($"MaxMessagesPerMinute ({callsPerMinute})", body.GetProperty("violatedLimit").GetString());
    }

    [Fact]
    public async Task Command_refused_before_running_costs_the_user_nothing()
    {
        int callsPerMinute = CallsPerMinute();
        string conversationId = await SeedConversationWithAgentAsync();

        // Twice the window of requests the channel got wrong: admission turns each away ahead of the limit
        for (int call = 1; call <= callsPerMinute; call++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await api.SendCommandAsync(
                conversationId, """{"name":"transmogrify"}""")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await api.SendCommandAsync(
                conversationId, """{"name":"compact","options":{"depth":"3"}}""")).StatusCode);
        }

        // The whole window is still there for the requests the user actually made
        for (int call = 1; call <= callsPerMinute; call++)
        {
            HttpResponseMessage response = await SendCompactAsync(conversationId);
            Assert.True(response.StatusCode == HttpStatusCode.Accepted,
                $"Command {call} of {callsPerMinute} was answered {(int)response.StatusCode}: refused requests were counted against the window.");
        }
    }

    [Fact]
    public async Task Message_meets_the_window_commands_filled()
    {
        int callsPerMinute = CallsPerMinute();
        string conversationId = await SeedConversationWithAgentAsync();

        for (int call = 1; call <= callsPerMinute; call++)
            Assert.Equal(HttpStatusCode.Accepted, (await SendCompactAsync(conversationId)).StatusCode);

        // One window per conversation, whatever the call: a command spent it, so the message is refused
        // at the gate and no turn is ever run
        HttpResponseMessage refused = await api.SendAsync(
            "POST", "/api/morgana/conversation/{id}/message", conversationId, api.HarnessToken());

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
    }

    [Fact]
    public async Task Command_past_the_window_is_told_as_its_own_outcome()
    {
        int callsPerMinute = CallsPerMinute();
        string conversationId = await SeedConversationWithAgentAsync(deliverToHarnessChannel: true);

        for (int call = 1; call <= callsPerMinute; call++)
            Assert.Equal(HttpStatusCode.Accepted, (await SendCompactAsync(conversationId)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await SendCompactAsync(conversationId)).StatusCode);

        // The refusal is the command's outcome: its finished frame, naming it, so a channel draws it where the
        // command's outcome goes and keeps it out of the transcript. The reason still travels for the channel to act on
        ChannelMessage refusal = await ReceiveRefusalAsync(conversationId);
        Assert.Equal("system", refusal.MessageType);
        Assert.NotNull(refusal.Progress);
        Assert.Equal("compact", refusal.Progress.Command);
        Assert.True(refusal.Progress.Finished, "The refusal of a command did not close the command's widget.");
        Assert.False(string.IsNullOrWhiteSpace(refusal.Text), "The refusal of a command told the user nothing.");
    }

    [Fact]
    public async Task Message_past_the_window_is_told_as_a_notice()
    {
        int callsPerMinute = CallsPerMinute();
        string conversationId = await SeedConversationWithAgentAsync(deliverToHarnessChannel: true);

        for (int call = 1; call <= callsPerMinute; call++)
            Assert.Equal(HttpStatusCode.Accepted, (await SendCompactAsync(conversationId)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await api.SendAsync(
            "POST", "/api/morgana/conversation/{id}/message", conversationId, api.HarnessToken())).StatusCode);

        // A refused message is a notice about the conversation, which a channel shows in it: no frame belongs to it
        ChannelMessage refusal = await ReceiveRefusalAsync(conversationId);
        Assert.Equal("system_warning", refusal.MessageType);
        Assert.Null(refusal.Progress);
    }

    /// <summary>
    /// A conversation on record with billing carrying it, so /compact is admitted and runs. Its deliveries reach
    /// the harness channel only when <paramref name="deliverToHarnessChannel"/> asks for them to be read.
    /// </summary>
    private async Task<string> SeedConversationWithAgentAsync(bool deliverToHarnessChannel = false)
    {
        string conversationId = ChannelApiClient.NewConversationId();
        await api.SeedConversationOnRecordAsync(conversationId, activeAgent: "billing",
            deliverToHarnessChannel ? fixture.Channel.CallbackUrl : null);
        return conversationId;
    }

    /// <summary>The rate limit's refusal among what the conversation was told, past the outcomes of the commands that filled the window.</summary>
    private async Task<ChannelMessage> ReceiveRefusalAsync(string conversationId)
    {
        while (true)
        {
            ChannelMessage delivered = await fixture.Channel.ReceiveAsync(conversationId, TimeSpan.FromSeconds(15));
            if (delivered.ErrorReason == "rate_limit_exceeded")
                return delivered;
        }
    }

    /// <summary>One /compact on the conversation, the call every test here counts with.</summary>
    private Task<HttpResponseMessage> SendCompactAsync(string conversationId) =>
        api.SendCommandAsync(conversationId, """{"name":"compact"}""");
}
