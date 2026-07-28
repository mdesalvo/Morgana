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

namespace PromptHarness.Infrastructure;

/// <summary>
/// The harness's own Morgana channel: a webhook receiver plus a REST client, self-issuing JWTs as
/// issuer <c>harness</c>.
/// </summary>
/// <remarks>
/// <para>It declares the <strong>full</strong> capability profile with no length budget, so
/// <c>MorganaChannelAdapter</c> short-circuits and every scenario measures Morgana's undegraded
/// output. Degradation is Rune's quadrant of the channel matrix and is deliberately not exercised
/// here — a suite that asserted on adapted text would be measuring the adapter, not the prompts.</para>
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
    /// Wires the channel against a running host.
    /// </summary>
    /// <param name="morganaBaseAddress">Base address of the instance under test.</param>
    /// <param name="issuerKey">Symmetric key the host accepts for the <c>harness</c> issuer.</param>
    /// <param name="audience">Audience claim Morgana validates.</param>
    public HarnessChannel(string morganaBaseAddress, string issuerKey, string audience)
    {
        this.morganaBaseAddress = morganaBaseAddress;
        this.audience = audience;

        credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(issuerKey)), SecurityAlgorithms.HmacSha256);

        httpClient = new HttpClient { BaseAddress = new Uri(morganaBaseAddress), Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>Starts the callback receiver on an ephemeral port.</summary>
    public async Task StartAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        receiver = builder.Build();

        receiver.MapPost("/harness-hook", async (HttpContext context) =>
        {
            ChannelMessage? message = await context.Request.ReadFromJsonAsync<ChannelMessage>();
            if (message is null)
                return Results.BadRequest();

            inbox.GetOrAdd(message.ConversationId, _ => Channel.CreateUnbounded<ChannelMessage>())
                 .Writer.TryWrite(message);

            return Results.Ok();
        });

        receiver.MapPost("/harness-hook/chunk", async (HttpContext context) =>
        {
            StreamChunkRequest? chunk = await context.Request.ReadFromJsonAsync<StreamChunkRequest>();
            if (chunk is null)
                return Results.BadRequest();

            chunks.GetOrAdd(chunk.ConversationId, _ => []).Add(chunk.ChunkText);

            return Results.Ok();
        });

        await receiver.StartAsync();

        string address = receiver.Services.GetRequiredService<IServer>()
                                 .Features.Get<IServerAddressesFeature>()!
                                 .Addresses.First();

        callbackUrl = $"{address.TrimEnd('/')}/harness-hook";
    }

    /// <summary>
    /// Opens a conversation and consumes the presentation message Morgana pushes on start, so the
    /// first asserted turn is genuinely the first user turn.
    /// </summary>
    /// <returns>The conversation id, and the presentation message that was drained.</returns>
    public async Task<(string ConversationId, ChannelMessage Presentation)> StartConversationAsync(TimeSpan timeout)
    {
        string conversationId = Guid.NewGuid().ToString("N");
        inbox[conversationId] = Channel.CreateUnbounded<ChannelMessage>();

        using HttpRequestMessage request = Authorized(HttpMethod.Post, "/api/morgana/conversation/start");
        request.Content = JsonContent.Create(new StartConversationRequest(
            ConversationId: conversationId,
            ChannelMetadata: BuildMetadata(callbackUrl)));

        using HttpResponseMessage response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

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

        return await ReceiveAsync(conversationId, timeout);
    }

    /// <summary>Ends a conversation and forgets its queues. Failures are swallowed: teardown is best-effort.</summary>
    public async Task EndConversationAsync(string conversationId)
    {
        try
        {
            using HttpRequestMessage request = Authorized(HttpMethod.Post, $"/api/morgana/conversation/{conversationId}/end");
            using HttpResponseMessage response = await httpClient.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            // The conversation is being discarded anyway.
        }
        finally
        {
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
        Channel<ChannelMessage> queue = inbox.GetOrAdd(conversationId, _ => Channel.CreateUnbounded<ChannelMessage>());

        using CancellationTokenSource cancellation = new CancellationTokenSource(timeout);
        try
        {
            return await queue.Reader.ReadAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
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

    /// <summary>The handshake: full capabilities, no length budget, webhook delivery.</summary>
    private static ChannelMetadata BuildMetadata(string callbackUrl) => new ChannelMetadata
    {
        Coordinates = new ChannelCoordinates
        {
            ChannelName = ChannelName,
            DeliveryMode = "webhook",
            CallbackUrl = callbackUrl
        },
        Capabilities = new ChannelCapabilities(
            SupportsRichCards: true,
            SupportsQuickReplies: true,
            SupportsStreaming: true,
            SupportsMarkdown: true,
            MaxMessageLength: null)
    };
}
