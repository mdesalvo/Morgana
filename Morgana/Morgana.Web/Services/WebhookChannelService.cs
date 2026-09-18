using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.Web.Services;

/// <summary>
/// Webhook implementation of IChannelService: POSTs ChannelMessages to callbackUrl declared in ChannelCoordinates
/// at conversation-start (deliveryMode=webhook). Callback URL loaded per-send from IChannelMetadataStore persisted data.
/// Does NOT sign POSTs (asymmetric trust: channel signs toward Morgana, not vice versa; mirrors GitHub/Stripe/Twilio conventions).
/// SendStreamChunkAsync POSTs to {callbackUrl}/chunk with minimal body. HTTP failures never fault the agent turn.
/// </summary>
/// <remarks>
/// A webhook is the one transport where Morgana itself sees a delivery fail, so it is the one that
/// delivers again: a message the callback could not take is re-sent on <see cref="RedeliveryDelays"/>
/// while the failure may pass, already adapted to the channel. A chunk is not: the final message
/// that follows carries the whole text anyway.
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
    /// The waits before each new attempt at a message the callback could not take, about half a
    /// minute in all: long enough to ride out a callback that is restarting or briefly unreachable,
    /// well inside the reply timeout a webhook channel gives a turn before declaring it lost.
    /// </summary>
    private static readonly TimeSpan[] RedeliveryDelays =
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)
    ];

    /// <summary>
    /// HTTP client factory used to rent a fresh <see cref="HttpClient"/> per send. Using a factory
    /// (instead of injecting a long-lived <see cref="HttpClient"/>) preserves the handler-rotation
    /// semantics that make <see cref="IHttpClientFactory"/> the supported pattern for singleton
    /// consumers — catching a typed client in a singleton would pin its handler forever.
    /// </summary>
    private readonly IHttpClientFactory httpClientFactory;

    /// <summary>
    /// Source of truth for per-conversation channel coordinates, settled at the handshake; queried
    /// here on every send to recover the callback URL for this conversation.
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
        // The callback this conversation announced at the handshake is where the message goes
        ChannelMetadata channelMetadata = await channelMetadataStore.GetChannelMetadataAsync(channelMessage.ConversationId);

        string? callbackUrl = channelMetadata.Coordinates.CallbackUrl;
        if (string.IsNullOrWhiteSpace(callbackUrl))
            throw new InvalidOperationException(
                $"Webhook dispatch for conversation {channelMessage.ConversationId} has no callbackUrl in coordinates; " +
                "the start-conversation gate should have rejected a deliveryMode=webhook handshake without an absolute callbackUrl.");

        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
        for (int failedAttempts = 0; ; failedAttempts++)
        {
            string failure;
            bool mayPass;
            try
            {
                using HttpResponseMessage response = await httpClient.PostAsJsonAsync(callbackUrl, channelMessage);
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "Webhook delivered to conversation {ConversationId} at {CallbackUrl}: type={Type}, agent={Agent}, completed={Completed}",
                        channelMessage.ConversationId, callbackUrl, channelMessage.MessageType, channelMessage.AgentName, channelMessage.AgentCompleted);
                    return;
                }

                failure = $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
                mayPass = MayPass(response.StatusCode);
            }
            catch (Exception ex)
            {
                // Unreachable, refused, timed out: the callback may be back in a moment
                failure = ex.Message;
                mayPass = true;
            }

            if (!mayPass || failedAttempts == RedeliveryDelays.Length)
            {
                logger.LogError(
                    "Webhook not delivered to conversation {ConversationId} at {CallbackUrl} after {Attempts} attempt(s): {Failure}",
                    channelMessage.ConversationId, callbackUrl, failedAttempts + 1, failure);
                return;
            }

            TimeSpan redeliveryDelay = RedeliveryDelays[failedAttempts];
            logger.LogWarning(
                "Webhook to conversation {ConversationId} at {CallbackUrl} failed ({Failure}); delivering again in {RedeliveryDelay}",
                channelMessage.ConversationId, callbackUrl, failure, redeliveryDelay);
            await Task.Delay(redeliveryDelay);
        }
    }

    /// <summary>
    /// Tells a refusal that may pass from one that will not. A server error, a timeout or a callback
    /// asking to slow down may clear up; any other client error is the callback's answer about this
    /// message (a channel no longer showing the conversation, say): repeating it changes nothing.
    /// </summary>
    private static bool MayPass(System.Net.HttpStatusCode statusCode) =>
        (int)statusCode >= 500
        || statusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests;

    /// <inheritdoc/>
    public async Task SendStreamChunkAsync(string conversationId, string chunkText)
    {
        // The callback this conversation announced at the handshake is where the chunk goes
        ChannelMetadata channelMetadata = await channelMetadataStore.GetChannelMetadataAsync(conversationId);

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
            // A lost chunk costs the user only the progressive reveal: the final message carries the
            // whole text and is delivered again. A cut callback loses every chunk of a turn, so one
            // line each, without the stack.
            logger.LogWarning(
                "Stream chunk not delivered to conversation {ConversationId} at {ChunkUrl}: {Failure}",
                conversationId, chunkUrl, ex.Message);
        }
    }
}