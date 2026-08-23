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
/// The harness's own Morgana channel: a webhook receiver plus a REST client, self-issuing JWTs as
/// issuer <c>harness</c>.
/// </summary>
/// <remarks>
/// <para>By default it declares the <strong>full</strong> capability profile with no length budget,
/// so <c>MorganaChannelAdapter</c> short-circuits and most scenarios measure Morgana's undegraded
/// output — a suite that asserted on adapted text by default would be measuring the adapter, not
/// the prompts. <see cref="StartConversationAsync"/> accepts an optional capability/channel-name
/// override for the one scenario group that deliberately wants the opposite: exercising
/// <c>MorganaChannelAdapter</c> itself (see <see cref="DegradedCapabilities"/>).</para>
///
/// <para>Delivery is <c>webhook</c> rather than <c>signalr</c> for the same reason Rune and Grimoire
/// use it: it needs no hub client, and the callback is an ordinary HTTP endpoint the harness can
/// host and correlate on.</para>
/// </remarks>
public sealed class HarnessChannel : IAsyncDisposable
{
    /// <summary>Channel name announced at the handshake, and the issuer name the JWTs carry.</summary>
    public const string ChannelName = "harness";

    /// <inheritdoc cref="ChannelName" />
    public const string IssuerName = ChannelName;

    /// <summary>
    /// Channel name for the one scenario group that opts into a degraded capability profile.
    /// Deliberately distinct from <see cref="ChannelName"/>: <c>LLMPresenterService</c> caches its
    /// presentation result process-wide, keyed only by channel name, so a degraded conversation
    /// reusing <c>"harness"</c> would race that cache against every other test in the assembly,
    /// depending on non-deterministic xUnit test ordering. <see cref="IssuerName"/> stays a
    /// separate compile-time constant, so varying this never touches JWT authentication.
    /// </summary>
    public const string DegradedChannelName = "harness-degraded";

    /// <summary>
    /// Mirrors Rune's "poor but honest" profile — no rich cards, no quick replies, no streaming, no
    /// markdown, 500-character budget — the contract surface <c>MorganaChannelAdapter</c> degrades
    /// toward, and the canonical capability set to actually engage its LLM rewrite path.
    /// </summary>
    public static readonly ChannelCapabilities DegradedCapabilities =
        new ChannelCapabilities(
            SupportsRichCards: false,
            SupportsQuickReplies: false,
            SupportsStreaming: false,
            SupportsMarkdown: false,
            MaxMessageLength: 500);

    /// <summary>Base address of the Morgana instance under test.</summary>
    private readonly string morganaBaseAddress;

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

    /// <summary>Streaming chunks per conversation; collected for diagnostics, never asserted on.</summary>
    private readonly ConcurrentDictionary<string, List<string>> chunks = new ConcurrentDictionary<string, List<string>>();

    /// <summary>Kestrel host serving the callback endpoints.</summary>
    private WebApplication? receiver;

    /// <summary>Absolute URL Morgana posts outbound messages to.</summary>
    private string callbackUrl = string.Empty;

    /// <summary>
    /// Whether <see cref="SendAsync"/> should spend a short grace wait draining a possible trailing
    /// side message after every turn's primary response — see
    /// <see cref="DiscardTrailingSideMessageAsync"/>'s own remarks. Off by default: the wait costs
    /// real wall-clock time on every single turn of every scenario in the assembly for a case
    /// (a dust-budget warning) that is otherwise never possible, since dust limiting is
    /// force-disabled everywhere except <c>DustTests</c>' own run.
    /// </summary>
    private readonly bool drainTrailingSideMessages;

    /// <summary>
    /// Wires the channel against a running host.
    /// </summary>
    /// <param name="morganaBaseAddress">Base address of the instance under test.</param>
    /// <param name="issuerKey">Symmetric key the host accepts for the <c>harness</c> issuer.</param>
    /// <param name="audience">Audience claim Morgana validates.</param>
    /// <param name="drainTrailingSideMessages">See the field's own remarks.</param>
    public HarnessChannel(string morganaBaseAddress, string issuerKey, string audience, bool drainTrailingSideMessages = false)
    {
        this.morganaBaseAddress = morganaBaseAddress;
        this.audience = audience;
        this.drainTrailingSideMessages = drainTrailingSideMessages;

        // The signing key here must be byte-identical to the one MorganaHostFixture pushed into
        // the host's Morgana:Authentication:Issuers:*:SymmetricKey — the two are minted from the
        // same value, this class just wraps it as HMAC-SHA256 credentials for token generation.
        credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(issuerKey)), SecurityAlgorithms.HmacSha256);

        httpClient = new HttpClient { BaseAddress = new Uri(morganaBaseAddress), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Starts the callback receiver on an ephemeral port.</summary>
    public async Task StartAsync()
    {
        // A second, tiny Kestrel instance, separate from the Morgana host itself — this is the
        // "webhook" side of the channel: Morgana pushes messages here rather than the harness
        // polling for them. Port 0 lets the OS pick a free ephemeral port, read back below.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        receiver = builder.Build();

        // Final delivery of a turn's response. Keyed into a per-conversation channel so multiple
        // conversations (and, if ever run concurrently, multiple in-flight turns) never cross wires.
        receiver.MapPost("/harness-hook", async (HttpContext context) =>
        {
            ChannelMessage? message = await context.Request.ReadFromJsonAsync<ChannelMessage>();
            if (message is null)
                return Results.BadRequest();

            inbox.GetOrAdd(message.ConversationId, _ => Channel.CreateUnbounded<ChannelMessage>())
                 .Writer.TryWrite(message);

            return Results.Ok();
        });

        // Streaming chunks arrive as Morgana composes its answer, ahead of the final message above.
        // Collected only for diagnostics — no scenario assertion reads them — so they are appended
        // to a plain list rather than routed through the same completion-signalling channel.
        receiver.MapPost("/harness-hook/chunk", async (HttpContext context) =>
        {
            StreamChunkRequest? chunk = await context.Request.ReadFromJsonAsync<StreamChunkRequest>();
            if (chunk is null)
                return Results.BadRequest();

            chunks.GetOrAdd(chunk.ConversationId, _ => []).Add(chunk.ChunkText);

            return Results.Ok();
        });

        await receiver.StartAsync();

        // Now that Kestrel has actually bound the ephemeral port, read back the concrete address —
        // this is the URL handed to Morgana at conversation-start time as the webhook callback.
        string address = receiver.Services.GetRequiredService<IServer>()
                                 .Features.Get<IServerAddressesFeature>()!
                                 .Addresses.First();

        callbackUrl = $"{address.TrimEnd('/')}/harness-hook";
    }

    /// <summary>
    /// Opens a conversation and consumes the presentation message Morgana pushes on start, so the
    /// first asserted turn is genuinely the first user turn.
    /// </summary>
    /// <param name="timeout">How long to wait for the presentation to arrive.</param>
    /// <param name="capabilities">
    /// Capability profile to announce; defaults to the full profile. Pass
    /// <see cref="DegradedCapabilities"/> to exercise <c>MorganaChannelAdapter</c>'s rewrite path.
    /// </param>
    /// <param name="channelName">
    /// Channel name to announce; defaults to <see cref="ChannelName"/>. Must be
    /// <see cref="DegradedChannelName"/> (or another distinct name) whenever <paramref name="capabilities"/>
    /// is overridden — see <see cref="DegradedChannelName"/>'s own remarks for why.
    /// </param>
    /// <returns>The conversation id, and the presentation message that was drained.</returns>
    public async Task<(string ConversationId, ChannelMessage Presentation)> StartConversationAsync(
        TimeSpan timeout, ChannelCapabilities? capabilities = null, string? channelName = null)
    {
        // The conversation id is minted here, client-side, and handed to Morgana on the start
        // call — the queue for it must exist before the request is sent, since the presentation
        // message can arrive over the webhook before this method reaches ReceiveAsync below.
        string conversationId = Guid.NewGuid().ToString("N");
        inbox[conversationId] = Channel.CreateUnbounded<ChannelMessage>();

        using HttpRequestMessage request = Authorized(HttpMethod.Post, "/api/morgana/conversation/start");
        request.Content = JsonContent.Create(new StartConversationRequest(
            ConversationId: conversationId,
            ChannelMetadata: BuildMetadata(callbackUrl, capabilities, channelName)));

        using HttpResponseMessage response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        // Every conversation opens with an unsolicited presentation message, pushed over the same
        // webhook a reply would use. Draining it here means the caller's first SendAsync genuinely
        // corresponds to the first user turn, not to whatever the presentation happened to be.
        ChannelMessage presentation = await ReceiveAsync(conversationId, timeout);

        return (conversationId, presentation);
    }

    /// <summary>Sends a user turn and waits for the message Morgana delivers in response.</summary>
    public async Task<ChannelMessage> SendAsync(string conversationId, string text, TimeSpan timeout)
    {
        using HttpRequestMessage request = Authorized(HttpMethod.Post, $"/api/morgana/conversation/{conversationId}/message");
        request.Content = JsonContent.Create(new SendMessageRequest(conversationId, text));

        using HttpResponseMessage response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        ChannelMessage primary = await ReceiveAsync(conversationId, timeout);
        if (drainTrailingSideMessages)
            await DiscardTrailingSideMessageAsync(conversationId);

        return primary;
    }

    /// <summary>
    /// Drains and discards a second, out-of-band message that can follow a turn's primary response
    /// on the very same webhook — <c>ConversationManagerActor</c>'s dust-budget warning or
    /// exhaustion notice, sent moments after the main answer. This channel's contract is one message
    /// per <see cref="SendAsync"/> call; left undrained, that second message would sit in the queue
    /// and be misread as the NEXT turn's response, silently shifting every assertion after it by one
    /// turn. Its content is never asserted on here — <c>DustTests</c> reads the fact of the warning
    /// from <c>ConversationManagerActor</c>'s own diagnostic log line instead (see
    /// <c>ExpectationChecker.CheckDust</c>) — so a short bounded wait to catch and drop it is enough;
    /// the grace period only needs to outlast the same async continuation the primary send() and this
    /// side-effect both run inside, not a whole extra LLM round trip.
    /// </summary>
    private async Task DiscardTrailingSideMessageAsync(string conversationId)
    {
        Channel<ChannelMessage> queue = inbox.GetOrAdd(conversationId, _ => Channel.CreateUnbounded<ChannelMessage>());

        using CancellationTokenSource cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await queue.Reader.ReadAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // The overwhelmingly common case: no side message followed this turn at all.
        }
    }

    /// <summary>Ends a conversation and forgets its queues. Failures are swallowed: teardown is best-effort.</summary>
    public async Task EndConversationAsync(string conversationId)
    {
        try
        {
            // Response is neither awaited for content nor checked for success — this call is a
            // courtesy notification to the host, not something a scenario's outcome depends on.
            using HttpRequestMessage request = Authorized(HttpMethod.Post, $"/api/morgana/conversation/{conversationId}/end");
            using HttpResponseMessage response = await httpClient.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            // The conversation is being discarded anyway.
        }
        finally
        {
            // Runs whether the end call succeeded or not: a run that fails mid-conversation still
            // needs its queues released, or they leak for the lifetime of the whole test assembly.
            inbox.TryRemove(conversationId, out _);
            chunks.TryRemove(conversationId, out _);
        }
    }

    /// <summary>Streaming chunks received so far for a conversation, for failure diagnostics.</summary>
    public IReadOnlyList<string> ChunksFor(string conversationId)
        => chunks.TryGetValue(conversationId, out List<string>? received) ? received : [];

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        httpClient.Dispose();

        if (receiver is not null)
            await receiver.DisposeAsync();
    }

    /// <summary>Dequeues the next inbound message for a conversation, or throws on timeout.</summary>
    private async Task<ChannelMessage> ReceiveAsync(string conversationId, TimeSpan timeout)
    {
        // GetOrAdd rather than a plain lookup: the webhook handler in StartAsync above may have
        // already created (and possibly already written to) this conversation's queue by the time
        // execution reaches here, since the two run on independent async paths.
        Channel<ChannelMessage> queue = inbox.GetOrAdd(conversationId, _ => Channel.CreateUnbounded<ChannelMessage>());

        using CancellationTokenSource cancellation = new CancellationTokenSource(timeout);
        try
        {
            // Blocks until the webhook handler writes a message for this conversation, or the
            // timeout fires — this is the actual wait for Morgana's asynchronous, out-of-band reply.
            return await queue.Reader.ReadAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Re-thrown as a plain TimeoutException with the callback URL attached: an
            // OperationCanceledException alone would not say whether the host hung, never called
            // back at all, or called back to the wrong address.
            throw new TimeoutException(
                $"No webhook delivery for conversation {conversationId} within {timeout.TotalSeconds:F0}s at {callbackUrl}.");
        }
    }

    /// <summary>Builds a request carrying a freshly-issued bearer token.</summary>
    private HttpRequestMessage Authorized(HttpMethod method, string path)
    {
        HttpRequestMessage request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateToken());

        return request;
    }

    /// <summary>Mints an HMAC-SHA256 JWT valid for five minutes, mirroring what every channel does.</summary>
    private string GenerateToken()
    {
        // Minted fresh per request rather than cached: the five-minute expiry is generous for a
        // single HTTP round trip, so there is no real cost to always issuing a brand-new token and
        // no need to track or refresh one that might have gone stale mid-run.
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
