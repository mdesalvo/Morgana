using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.Web.Services;

/// <summary>
/// Webhook implementation of IChannelService: POSTs ChannelMessages to callbackUrl declared in ChannelCoordinates
/// at conversation-start (deliveryMode=webhook). Callback URL loaded per-send from IChannelMetadataStore persisted data.
/// Does NOT sign POSTs (asymmetric trust: channel signs toward Morgana, not vice versa; mirrors GitHub/Stripe/Twilio conventions).
/// SendStreamChunkAsync POSTs to {callbackUrl}/chunk with minimal body. HTTP failures logged and swallowed (no agent fault).
/// </summary>
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
}