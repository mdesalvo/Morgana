using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Morgana.Contracts;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>
/// The harness's own Morgana channel: a webhook receiver plus a REST client, self-issuing JWTs as issuer <c>harness</c>.
/// It announces the full capabilities, so <c>MorganaChannelAdapter</c> passes every message untouched and the scenarios measure the prompts rather than the adapter;
/// delivery is a webhook, as for Rune and Grimoire, since it needs no hub client and the callback is an endpoint the harness hosts itself.
/// </summary>
public sealed class HarnessChannel : IAsyncDisposable
{
    /// <summary>Channel name announced at the handshake and the issuer name the JWTs carry.</summary>
    public const string ChannelName = "harness";

    /// <inheritdoc cref="ChannelName" />
    public const string IssuerName = ChannelName;

    /// <summary>
    /// The channel name of the one scenario group that opts into the degraded capabilities.
    /// <c>LLMPresenterService</c> caches its presentation process-wide by channel name, so a degraded conversation reusing <c>harness</c>
    /// would race that cache against every other test; <see cref="IssuerName"/> is a constant of its own, so this never touches authentication.
    /// </summary>
    public const string DegradedChannelName = "harness-degraded";

    /// <summary>
    /// Rune's poor but honest capabilities: no rich cards, no quick replies, no streaming, no markdown and 500 characters.
    /// It is the set that engages <c>MorganaChannelAdapter</c>'s model rewrite.
    /// </summary>
    public static readonly ChannelCapabilities DegradedCapabilities =
        new ChannelCapabilities(
            SupportsRichCards: false,
            SupportsQuickReplies: false,
            SupportsStreaming: false,
            SupportsMarkdown: false,
            MaxMessageLength: 500);

    /// <summary>HMAC-SHA256 credentials matching the <c>harness</c> issuer key the host was started with.</summary>
    private readonly SigningCredentials credentials;

    /// <summary>Audience Morgana validates the tokens against.</summary>
    private readonly string audience;

    /// <summary>Reusable token writer; <see cref="JsonWebTokenHandler"/> is thread-safe.</summary>
    private readonly JsonWebTokenHandler tokenHandler = new JsonWebTokenHandler();

    /// <summary>Client for the REST surface, with a fresh bearer token per request.</summary>
    private readonly HttpClient httpClient;

    /// <summary>Inbound queue per conversation, fed by the webhook endpoint.</summary>
    private readonly ConcurrentDictionary<string, Channel<ChannelMessage>> inbox = new ConcurrentDictionary<string, Channel<ChannelMessage>>();

    /// <summary>Kestrel host serving the callback endpoints.</summary>
    private WebApplication? receiver;

    /// <summary>Absolute URL Morgana posts outbound messages to.</summary>
    private string callbackUrl = string.Empty;

    /// <summary>Where this channel takes Morgana's deliveries, for a conversation seeded on record to name as its callback.</summary>
    public string CallbackUrl => callbackUrl;

    /// <summary>
    /// Whether <see cref="SendAsync"/> waits briefly after each turn's answer to drain a trailing notice (see <see cref="DiscardTrailingSideMessageAsync"/>).
    /// It is off by default: the wait costs time on every turn for a dust notice that only <c>DustTests</c>' own run can produce.
    /// </summary>
    private readonly bool drainTrailingSideMessages;

    /// <summary>Wires the channel against a running host.</summary>
    /// <param name="morganaBaseAddress">Base address of the instance under test.</param>
    /// <param name="issuerKey">Symmetric key the host accepts for the <c>harness</c> issuer.</param>
    /// <param name="audience">Audience claim Morgana validates.</param>
    /// <param name="drainTrailingSideMessages">Whether each turn waits to drain a trailing dust notice.</param>
    public HarnessChannel(string morganaBaseAddress, string issuerKey, string audience, bool drainTrailingSideMessages = false)
    {
        // The audience every token names and whether turns wait for a trailing notice.
        this.audience = audience;
        this.drainTrailingSideMessages = drainTrailingSideMessages;

        // The same key MorganaHostFixture gave the host for the harness issuer, as HMAC-SHA256 signing credentials.
        credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(issuerKey)), SecurityAlgorithms.HmacSha256);

        // The REST calls only queue work, so thirty seconds is generous; the answers arrive on the webhook.
        httpClient = new HttpClient { BaseAddress = new Uri(morganaBaseAddress), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Starts the callback receiver on an ephemeral port.</summary>
    public async Task StartAsync()
    {
        // A small web application of its own, the webhook side of the channel: Morgana pushes here and the harness never polls.
        // The system picks the port, read back once it is bound; the application's log would only crowd the host output the suite reads.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        receiver = builder.Build();

        // A turn's final message, queued per conversation so two conversations never cross wires.
        receiver.MapPost("/harness-hook", async (HttpContext context) =>
        {
            // A body that is not a channel message is refused as a channel would refuse it.
            ChannelMessage? message = await context.Request.ReadFromJsonAsync<ChannelMessage>();
            if (message is null)
                return Results.BadRequest();

            // The conversation's queue may not exist yet, when the delivery beats the start call's return.
            inbox.GetOrAdd(message.ConversationId, _ => Channel.CreateUnbounded<ChannelMessage>())
                 .Writer.TryWrite(message);

            // Taken, so Morgana does not deliver it again.
            return Results.Ok();
        });

        // Streaming chunks precede the final message, which carries the whole text: they are acknowledged and dropped,
        // since no scenario reads them and an unanswered chunk would only fill the host log with failed deliveries.
        receiver.MapPost("/harness-hook/chunk", async (HttpContext context) =>
        {
            // A body that is not a chunk is refused as a channel would refuse it.
            StreamChunkRequest? chunk = await context.Request.ReadFromJsonAsync<StreamChunkRequest>();
            return chunk is null ? Results.BadRequest() : Results.Ok();
        });

        await receiver.StartAsync();

        // The address the system bound, which every conversation announces as its webhook callback.
        string address = receiver.Services.GetRequiredService<IServer>()
                                 .Features.Get<IServerAddressesFeature>()!
                                 .Addresses.First();

        callbackUrl = $"{address.TrimEnd('/')}/harness-hook";
    }

    /// <summary>Opens a conversation and consumes the presentation Morgana pushes on start, so the first asserted turn is the first user turn.</summary>
    /// <param name="timeout">How long to wait for the presentation to arrive.</param>
    /// <param name="capabilities">
    /// Capability profile to announce; defaults to the full profile. Pass
    /// <see cref="DegradedCapabilities"/> to exercise <c>MorganaChannelAdapter</c>'s rewrite path.
    /// </param>
    /// <param name="channelName">Channel name to announce, <see cref="ChannelName"/> by default; a distinct one, such as <see cref="DegradedChannelName"/>, whenever <paramref name="capabilities"/> is overridden.</param>
    /// <returns>The conversation id and the presentation message that was drained.</returns>
    public async Task<(string ConversationId, ChannelMessage Presentation)> StartConversationAsync(
        TimeSpan timeout, ChannelCapabilities? capabilities = null, string? channelName = null)
    {
        // The channel mints the id and its queue exists before the start call, since the presentation can arrive before the call returns.
        string conversationId = Guid.NewGuid().ToString("N");
        inbox[conversationId] = Channel.CreateUnbounded<ChannelMessage>();

        // The handshake: the id, the channel's name and capabilities and the callback just bound.
        using HttpRequestMessage request = Authorized(HttpMethod.Post, "/api/morgana/conversation/start");
        request.Content = JsonContent.Create(new StartConversationRequest(
            ConversationId: conversationId,
            ChannelMetadata: BuildMetadata(callbackUrl, capabilities, channelName)));

        // A refused start fails the run here rather than as a timeout on the presentation.
        using HttpResponseMessage response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // The welcome arrives over the same webhook a reply uses, so it is drained before the first turn reads its answer.
        ChannelMessage presentation = await ReceiveAsync(conversationId, timeout);

        // The id every later call names and the welcome, whose buttons a first pressed turn reads.
        return (conversationId, presentation);
    }

    /// <summary>How a message Morgana speaks herself is signed, spelled out: it is read off the wire, not shared code.</summary>
    private const string MorganaAgentName = "Morgana";

    /// <summary>Sends a user turn and waits for the message Morgana delivers in response.</summary>
    public async Task<ChannelMessage> SendAsync(string conversationId, string text, TimeSpan timeout)
    {
        // The user's message, as a channel posts it.
        using HttpRequestMessage request = Authorized(HttpMethod.Post, $"/api/morgana/conversation/{conversationId}/message");
        request.Content = JsonContent.Create(new SendMessageRequest(text));

        // A refused message fails the run here rather than as a timeout on the answer.
        using HttpResponseMessage response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // The turn's answer, delivered on the webhook.
        ChannelMessage primary = await ReceiveAsync(conversationId, timeout);

        // An agent that finished hands the conversation back to Morgana, who says so in a message of
        // her own right behind its answer. It belongs to this turn: left queued, the next turn would
        // read it as its own reply.
        if (primary.AgentCompleted && !string.Equals(primary.AgentName, MorganaAgentName, StringComparison.Ordinal))
            await ReceiveAsync(conversationId, timeout);

        // Only DustTests' run can produce a dust notice after the answer, which would otherwise be read as the next turn's.
        if (drainTrailingSideMessages)
            await DiscardTrailingSideMessageAsync(conversationId);

        // The answer the turn is asserted on.
        return primary;
    }

    /// <summary>
    /// Drains and drops the dust notice <c>ConversationManagerActor</c> may send moments after a turn's answer on the same webhook.
    /// Left queued it would be read as the next turn's answer, shifting every later assertion; <c>DustTests</c> reads the notice from the host log instead.
    /// </summary>
    private async Task DiscardTrailingSideMessageAsync(string conversationId)
    {
        // The conversation's queue, created by the delivery or by the start.
        Channel<ChannelMessage> queue = inbox.GetOrAdd(conversationId, _ => Channel.CreateUnbounded<ChannelMessage>());

        // The manager sends the notice right after the answer, so a short wait is enough and costs no model call.
        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            // A notice that arrives is dropped.
            await queue.Reader.ReadAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // The overwhelmingly common case: no side message followed this turn at all.
        }
    }

    /// <summary>
    /// Reads the conversation's persisted history over the REST surface a resuming channel uses: the one place a consultation's absence shows.
    /// Spans and tool logs record what happened during a turn, never what survived it.
    /// </summary>
    public async Task<IReadOnlyList<MorganaChatMessage>> GetHistoryAsync(string conversationId)
    {
        // The history endpoint, as a channel catching up asks it.
        using HttpRequestMessage request = Authorized(HttpMethod.Get, $"/api/morgana/conversation/{conversationId}/history");

        // A refused read fails the turn that asked for it.
        using HttpResponseMessage response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // The transcript wrapped in the response.
        ConversationHistoryResponse? history = await response.Content.ReadFromJsonAsync<ConversationHistoryResponse>();

        // Empty while nobody has spoken.
        return history?.Messages ?? [];
    }

    /// <summary>Ends a conversation and forgets its queue, swallowing failures since teardown is best-effort.</summary>
    public async Task EndConversationAsync(string conversationId)
    {
        try
        {
            // Neither the answer's body nor its status is read: ending is a courtesy to the host no outcome depends on.
            using HttpRequestMessage request = Authorized(HttpMethod.Post, $"/api/morgana/conversation/{conversationId}/end");
            using HttpResponseMessage response = await httpClient.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            // The conversation is being discarded anyway.
        }
        finally
        {
            // The queue goes however the call went: a run failing mid-conversation would otherwise leak it for the whole assembly.
            inbox.TryRemove(conversationId, out _);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // The REST client and the receiver go together at the end of the run.
        httpClient.Dispose();

        // A channel never started has no receiver to stop.
        if (receiver is not null)
            await receiver.DisposeAsync();
    }

    /// <summary>Dequeues the next inbound message for a conversation, throwing once the timeout passes.</summary>
    public async Task<ChannelMessage> ReceiveAsync(string conversationId, TimeSpan timeout)
    {
        // The webhook handler may already have created this conversation's queue and written to it.
        Channel<ChannelMessage> queue = inbox.GetOrAdd(conversationId, _ => Channel.CreateUnbounded<ChannelMessage>());

        // The turn's budget bounds the wait for Morgana's reply.
        using CancellationTokenSource cancellation = new CancellationTokenSource(timeout);
        try
        {
            // The next message the webhook queued for this conversation, which is the actual wait for Morgana's reply.
            return await queue.Reader.ReadAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // The timeout names the callback, so a reader can tell a hung host from a delivery sent elsewhere.
            throw new TimeoutException(
                $"No webhook delivery for conversation {conversationId} within {timeout.TotalSeconds:F0}s at {callbackUrl}.");
        }
    }

    /// <summary>Builds a request carrying a freshly-issued bearer token.</summary>
    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        // Every call carries a fresh token, as a channel's every call does.
        HttpRequestMessage request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateToken());

        // The request, for the caller to give a body.
        return request;
    }

    /// <summary>Mints an HMAC-SHA256 JWT valid for five minutes, mirroring what every channel does.</summary>
    private string GenerateToken()
    {
        // A token per request never goes stale during a long run: five minutes is ample for one round trip.
        SecurityTokenDescriptor descriptor = new SecurityTokenDescriptor
        {
            Issuer = IssuerName,
            Audience = audience,
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, "harness"),
                new Claim(JwtRegisteredClaimNames.Name, "Harness")
            ]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = credentials
        };

        // The signed token, sent as the bearer.
        return tokenHandler.CreateToken(descriptor);
    }

    /// <summary>The handshake: full capabilities and no length budget by default, webhook delivery.</summary>
    private static ChannelMetadata BuildMetadata(string callbackUrl, ChannelCapabilities? capabilities = null, string? channelName = null) => new ChannelMetadata
    {
        Coordinates = new ChannelCoordinates
        {
            ChannelName = channelName ?? ChannelName,
            DeliveryMode = "webhook",
            CallbackUrl = callbackUrl
        },
        Capabilities = capabilities ?? new ChannelCapabilities(
            SupportsRichCards: true,
            SupportsQuickReplies: true,
            SupportsStreaming: true,
            SupportsMarkdown: true,
            MaxMessageLength: null)
    };
}