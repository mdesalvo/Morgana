using Morgana.AI.Interfaces;
using Morgana.Contracts;
using static Morgana.AI.Records;

namespace Morgana.Web.Services;

/// <summary>
/// HTTP webhook implementation of <see cref="IChannelService"/>. POSTs outbound
/// <see cref="ChannelMessage"/>s to the absolute <c>callbackUrl</c> the channel declared on its
/// <see cref="ChannelCoordinates"/> at the conversation-start handshake. Serves the
/// <c>deliveryMode=webhook</c> dispatch key.
/// </summary>
/// <remarks>
/// <para><strong>Addressing:</strong></para>
/// <para>Morgana does not hold per-channel addressing state outside <see cref="IChannelMetadataStore"/>:
/// the callback URL lives on the persisted <see cref="ChannelCoordinates"/>, so this service
/// looks it up per send. A missing URL at dispatch time is an invariant violation — the
/// start-conversation gate rejects webhook handshakes without an absolute callbackUrl.</para>
///
/// <para><strong>Trust model (outbound):</strong></para>
/// <para>Morgana does NOT sign the POST it sends to the channel. Authentication is asymmetric by
/// design: the channel signs toward Morgana (per-issuer JWT), not the other way around. A webhook
/// client that wants to be sure the request came from its Morgana instance is expected to rely on
/// network-level trust (mTLS, private VPC, IP allowlist) and/or a shared header secret configured
/// on its own side — Morgana's position here mirrors GitHub/Stripe/Twilio webhook conventions.</para>
///
/// <para><strong>Streaming:</strong></para>
/// <para><see cref="SendStreamChunkAsync"/> POSTs each chunk to <c>{callbackUrl}/chunk</c> with a
/// minimal body (<c>{ conversationId, chunkText }</c>) mirroring SignalR's
/// <c>ReceiveStreamChunk</c> contract. The endpoint suffix is a convention: a channel that
/// declares <see cref="ChannelCapabilities.SupportsStreaming"/> = <see langword="true"/> over
/// webhook is expected to expose it. Channels that opt out simply declare streaming off and the
/// supervisor never invokes this method.</para>
///
/// <para><strong>Reliability:</strong></para>
/// <para>HTTP failures are logged and swallowed so a misbehaving callback target can't fault the
/// agent turn. The concrete transport is free to time out or return non-success; the conversation
/// keeps running.</para>
/// </remarks>
public class WebhookChannelService : IChannelService
{
    /// <summary>
    /// Named key used when renting an <see cref="HttpClient"/> from <see cref="IHttpClientFactory"/>.
    /// Keeps the outbound webhook HTTP pipeline configurable independently of other HTTP clients
    /// in the app (timeouts, handlers, telemetry).
    /// </summary>
    internal const string HttpClientName = "Morgana.Webhook";

    /// <summary>
    /// HTTP client factory used to rent a fresh <see cref="HttpClient"/> per send. Using a factory
    /// (instead of injecting a long-lived <see cref="HttpClient"/>) preserves the handler-rotation
    /// semantics that make <see cref="IHttpClientFactory"/> the supported pattern for singleton
    /// consumers — catching a typed client in a singleton would pin its handler forever.
    /// </summary>
    private readonly IHttpClientFactory httpClientFactory;

    /// <summary>
    /// Source of truth for per-conversation channel coordinates. Populated by
    /// <c>ConversationManagerActor</c> at handshake; queried here on every send to recover the
    /// callback URL for this conversation without duplicating addressing state.
    /// </summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>
    /// Logger for diagnostic output. Emits an info entry per successful POST and error entries
    /// when the callback target rejects the delivery or is unreachable.
    /// </summary>
    private readonly ILogger<WebhookChannelService> logger;

    /// <summary>
    /// Initialises a new instance of the <see cref="WebhookChannelService"/>.
    /// </summary>
    public WebhookChannelService(
        IHttpClientFactory httpClientFactory,
        IChannelMetadataStore channelMetadataStore,
        ILogger<WebhookChannelService> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.channelMetadataStore = channelMetadataStore;
        this.logger = logger;
    }

    /// <inheritdoc/>
    public async Task SendMessageAsync(ChannelMessage channelMessage)
    {
        if (!channelMetadataStore.TryGetChannelMetadata(channelMessage.ConversationId, out ChannelMetadata? channelMetadata))
            throw new InvalidOperationException(
                $"No channel metadata registered for conversation {channelMessage.ConversationId}; " +
                "the start-conversation gate should have ensured registration before any webhook dispatch.");

        string? callbackUrl = channelMetadata.Coordinates.CallbackUrl;
        if (string.IsNullOrWhiteSpace(callbackUrl))
            throw new InvalidOperationException(
                $"Webhook dispatch for conversation {channelMessage.ConversationId} has no callbackUrl in coordinates; " +
                "the start-conversation gate should have rejected a deliveryMode=webhook handshake without an absolute callbackUrl.");

        try
        {
            HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(callbackUrl, channelMessage);
            if (!response.IsSuccessStatusCode)
            {
                string body = await response.Content.ReadAsStringAsync();
                logger.LogError(
                    "Webhook callback returned {StatusCode} for conversation {ConversationId} at {CallbackUrl}: {Body}",
                    (int)response.StatusCode, channelMessage.ConversationId, callbackUrl, body);
                return;
            }

            logger.LogInformation(
                "Webhook delivered to conversation {ConversationId} at {CallbackUrl}: type={Type}, agent={Agent}, completed={Completed}",
                channelMessage.ConversationId, callbackUrl, channelMessage.MessageType, channelMessage.AgentName, channelMessage.AgentCompleted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to deliver webhook for conversation {ConversationId} at {CallbackUrl}",
                channelMessage.ConversationId, callbackUrl);
        }
    }

    /// <inheritdoc/>
    public async Task SendStreamChunkAsync(string conversationId, string chunkText)
    {
        if (!channelMetadataStore.TryGetChannelMetadata(conversationId, out ChannelMetadata? channelMetadata))
            throw new InvalidOperationException(
                $"No channel metadata registered for conversation {conversationId}; " +
                "the start-conversation gate should have ensured registration before any stream chunk dispatch.");

        string? callbackUrl = channelMetadata.Coordinates.CallbackUrl;
        if (string.IsNullOrWhiteSpace(callbackUrl))
            throw new InvalidOperationException(
                $"Webhook stream-chunk dispatch for conversation {conversationId} has no callbackUrl in coordinates; " +
                "the start-conversation gate should have rejected a deliveryMode=webhook handshake without an absolute callbackUrl.");

        // Convention: chunks land at "{callbackUrl}/chunk". Channels that advertise
        // SupportsStreaming=true over webhook are expected to expose this path; the alternative
        // (a separate streamCallbackUrl on coordinates) would double the handshake surface for
        // no real flexibility — the path suffix is enough.
        string chunkUrl = callbackUrl.TrimEnd('/') + "/chunk";

        try
        {
            HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
                chunkUrl, new StreamChunkRequest(conversationId, chunkText));
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    "Webhook chunk callback returned {StatusCode} for conversation {ConversationId} at {ChunkUrl}",
                    (int)response.StatusCode, conversationId, chunkUrl);
            }
        }
        catch (Exception ex)
        {
            // Same reliability contract as SendMessageAsync: a misbehaving callback target must not fault the agent turn.
            logger.LogError(ex,
                "Failed to deliver stream chunk for conversation {ConversationId} at {ChunkUrl}",
                conversationId, chunkUrl);
        }
    }

    /// <summary>
    /// Minimal wire shape for the chunk endpoint. Mirrors SignalR's <c>ReceiveStreamChunk</c>
    /// payload — conversation id plus the incremental chunk text.
    /// </summary>
    private sealed record StreamChunkRequest(string ConversationId, string ChunkText);
}