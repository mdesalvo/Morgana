using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.Web.Services;

/// <summary>
/// The transport of deliveryMode <c>webhook</c>: POSTs a conversation's messages to the callbackUrl its channel declared, its chunks to <c>{callbackUrl}/chunk</c>.
/// It is the one transport where Morgana sees a delivery fail, so it delivers a message again while the failure may pass; a chunk is never
/// redelivered, since the final message carries the whole text. The POSTs are not signed: the channel proves itself to Morgana, not the reverse.
/// </summary>
public class WebhookChannelService : IChannelService
{
    /// <summary>The name of the webhook's HTTP client, so its timeouts and handlers are configured apart from any other client of the host.</summary>
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
    /// Hands out a client per send.
    /// This service is a singleton, so a client held for its lifetime would pin one handler and miss every DNS change of a callback.
    /// </summary>
    private readonly IHttpClientFactory httpClientFactory;

    /// <summary>The handshake on record, read on every send for the conversation's callback.</summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>Records every delivery, every redelivery and every message finally lost.</summary>
    private readonly ILogger<WebhookChannelService> logger;

    /// <summary>Binds the transport to its HTTP clients and to the handshake on record.</summary>
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

        // Start refuses a webhook handshake without an absolute callback, so a missing one is a record nobody should have written.
        string? callbackUrl = channelMetadata.Coordinates.CallbackUrl;
        if (string.IsNullOrWhiteSpace(callbackUrl))
            throw new InvalidOperationException(
                $"Webhook dispatch for conversation {channelMessage.ConversationId} has no callbackUrl in coordinates; " +
                "the start-conversation gate should have rejected a deliveryMode=webhook handshake without an absolute callbackUrl.");

        // One client for every attempt at this message.
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);

        // Attempts go on until one succeeds, the failure will not pass or the redelivery delays are spent.
        for (int failedAttempts = 0; ; failedAttempts++)
        {
            // What went wrong on this attempt and whether trying again could change it.
            string failure;
            bool mayPass;
            try
            {
                // The message as the channel's contract defines it, already fitted to the channel.
                using HttpResponseMessage response = await httpClient.PostAsJsonAsync(callbackUrl, channelMessage);

                // Taken by the callback: the delivery is done.
                if (response.IsSuccessStatusCode)
                {
                    logger.LogInformation(
                        "Webhook delivered to conversation {ConversationId} at {CallbackUrl}: type={Type}, agent={Agent}, completed={Completed}",
                        channelMessage.ConversationId, callbackUrl, channelMessage.MessageType, channelMessage.AgentName, channelMessage.AgentCompleted);
                    return;
                }

                // The callback answered with a refusal, whose status says whether it may clear up.
                failure = $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}";
                mayPass = MayPass(response.StatusCode);
            }
            catch (Exception ex)
            {
                // Unreachable, refused, timed out: the callback may be back in a moment
                failure = ex.Message;
                mayPass = true;
            }

            // A refusal that will not pass ends the attempts, as does the last delay spent: the channel catches up from the history.
            if (!mayPass || failedAttempts == RedeliveryDelays.Length)
            {
                logger.LogError(
                    "Webhook not delivered to conversation {ConversationId} at {CallbackUrl} after {Attempts} attempt(s): {Failure}",
                    channelMessage.ConversationId, callbackUrl, failedAttempts + 1, failure);
                return;
            }

            // Each wait longer than the one before, so a callback that is restarting gets the time to come back.
            TimeSpan redeliveryDelay = RedeliveryDelays[failedAttempts];
            logger.LogWarning(
                "Webhook to conversation {ConversationId} at {CallbackUrl} failed ({Failure}); delivering again in {RedeliveryDelay}",
                channelMessage.ConversationId, callbackUrl, failure, redeliveryDelay);
            await Task.Delay(redeliveryDelay);
        }
    }

    /// <summary>
    /// Tells a refusal that may pass from one that will not: a server error, a timeout or a request to slow down may clear up.
    /// Any other client error is the callback's answer about this message, such as a channel that has closed the conversation, which repeating it does not change.
    /// </summary>
    private static bool MayPass(System.Net.HttpStatusCode statusCode) =>
        (int)statusCode >= 500
        || statusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests;

    /// <inheritdoc/>
    public async Task SendStreamChunkAsync(string conversationId, string chunkText)
    {
        // The callback this conversation announced at the handshake is where the chunk goes
        ChannelMetadata channelMetadata = await channelMetadataStore.GetChannelMetadataAsync(conversationId);

        // Missing only on a record that start should have refused, as for a message.
        string? callbackUrl = channelMetadata.Coordinates.CallbackUrl;
        if (string.IsNullOrWhiteSpace(callbackUrl))
            throw new InvalidOperationException(
                $"Webhook stream-chunk dispatch for conversation {conversationId} has no callbackUrl in coordinates; " +
                "the start-conversation gate should have rejected a deliveryMode=webhook handshake without an absolute callbackUrl.");

        // A channel streaming over webhook takes its chunks at "{callbackUrl}/chunk": a fixed suffix spares the
        // handshake a second address that would buy no flexibility.
        string chunkUrl = callbackUrl.TrimEnd('/') + "/chunk";

        try
        {
            // One attempt only: the final message follows with the whole text.
            HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
                chunkUrl, new StreamChunkRequest(conversationId, chunkText));

            // A refused chunk is recorded and the turn streams on.
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