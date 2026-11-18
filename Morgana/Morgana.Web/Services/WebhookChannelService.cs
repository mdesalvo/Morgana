using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Morgana.AI;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.Web.Services;

/// <summary>The transport of deliveryMode <c>webhook</c>: POSTs a conversation's messages to the callbackUrl its channel declared and its chunks to <c>{callbackUrl}/chunk</c>, each signed as <see cref="WebhookAuthentication"/> describes.</summary>
/// <remarks>It is the one transport where Morgana sees a delivery fail, so a message is delivered again while the failure may pass and a chunk never is, since the final message carries the whole text.</remarks>
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

    /// <summary>How long a delivery token is good for, the same five minutes a channel's own tokens toward Morgana live.</summary>
    private static readonly TimeSpan DeliveryTokenLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Hands out a client per send.
    /// This service is a singleton, so a client held for its lifetime would pin one handler and miss every DNS change of a callback.
    /// </summary>
    private readonly IHttpClientFactory httpClientFactory;

    /// <summary>The handshake on record, read on every send for the conversation's callback and the channel it names.</summary>
    private readonly IChannelMetadataStore channelMetadataStore;

    /// <summary>The channels admitted, read on every send for the key of the channel a delivery goes to.</summary>
    private readonly Records.AuthenticationOptions authenticationOptions;

    /// <summary>Writes the delivery tokens; it holds no state, so every send shares it.</summary>
    private readonly JsonWebTokenHandler tokenHandler = new JsonWebTokenHandler();

    /// <summary>Records every delivery, every redelivery and every message finally lost.</summary>
    private readonly ILogger<WebhookChannelService> logger;

    /// <summary>Binds the transport to its HTTP clients, to the handshake on record and to the channels' keys.</summary>
    public WebhookChannelService(
        IHttpClientFactory httpClientFactory,
        IChannelMetadataStore channelMetadataStore,
        IOptions<Records.AuthenticationOptions> authenticationOptions,
        ILogger<WebhookChannelService> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.channelMetadataStore = channelMetadataStore;
        this.authenticationOptions = authenticationOptions.Value;
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

        // The message as the channel's contract defines it, already fitted to the channel, fixed once as bytes: the token
        // vouches for these exact bytes and every attempt sends them unchanged, so no second encoding can disagree with it.
        byte[] deliveryBody = JsonSerializer.SerializeToUtf8Bytes(channelMessage, JsonSerializerOptions.Web);

        // One client for every attempt at this message.
        HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);

        // Attempts go on until one succeeds, the failure will not pass or the redelivery delays are spent.
        for (int failedAttempts = 0; ; failedAttempts++)
        {
            // A token per attempt, since an attempt held by a slow callback can outlast the lifetime of one minted before the first.
            // A channel whose key is gone is told nothing rather than something unsigned, which it would rightly refuse.
            string? deliveryToken = MintDeliveryToken(channelMetadata.Coordinates.ChannelName, deliveryBody);
            if (deliveryToken is null)
            {
                logger.LogError(
                    "Webhook not delivered to conversation {ConversationId}: no key is configured for channel '{ChannelName}' under Morgana:Authentication:Issuers",
                    channelMessage.ConversationId, channelMetadata.Coordinates.ChannelName);
                return;
            }

            // What went wrong on this attempt and whether trying again could change it.
            string failure;
            bool mayPass;
            try
            {
                // The signed delivery goes out as one POST.
                using HttpRequestMessage deliveryRequest = CreateDeliveryRequest(callbackUrl, deliveryBody, deliveryToken);
                using HttpResponseMessage response = await httpClient.SendAsync(deliveryRequest);

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

        // Each chunk is a POST of its own, so it carries a token of its own over its own bytes.
        byte[] chunkBody = JsonSerializer.SerializeToUtf8Bytes(new StreamChunkRequest(conversationId, chunkText), JsonSerializerOptions.Web);

        // A channel whose key is gone gets no chunk, as it gets no message.
        string? chunkToken = MintDeliveryToken(channelMetadata.Coordinates.ChannelName, chunkBody);
        if (chunkToken is null)
        {
            logger.LogWarning(
                "Stream chunk not delivered to conversation {ConversationId}: no key is configured for channel '{ChannelName}' under Morgana:Authentication:Issuers",
                conversationId, channelMetadata.Coordinates.ChannelName);
            return;
        }

        try
        {
            // One attempt only: the final message follows with the whole text.
            HttpClient httpClient = httpClientFactory.CreateClient(HttpClientName);
            using HttpRequestMessage chunkRequest = CreateDeliveryRequest(chunkUrl, chunkBody, chunkToken);
            using HttpResponseMessage response = await httpClient.SendAsync(chunkRequest);

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

    /// <summary>Signs a delivery token for one body bound for one channel; null when no key is configured for that channel.</summary>
    private string? MintDeliveryToken(string channelName, byte[] deliveryBody)
    {
        // Start admits a webhook channel only under the issuer its token proved, so the name on record finds that issuer's key.
        string? channelKey = authenticationOptions.Issuers
            .FirstOrDefault(issuer => string.Equals(issuer.Name, channelName, StringComparison.Ordinal))?.SymmetricKey;

        // A channel removed from configuration after its conversation started has no key left to sign with.
        if (string.IsNullOrWhiteSpace(channelKey))
            return null;

        // The channel's own key proves the delivery is Morgana's, its name as audience keeps a token for one channel from
        // opening another and the body's hash keeps a captured token from vouching for any other body.
        SecurityTokenDescriptor deliveryTokenDescriptor = new SecurityTokenDescriptor
        {
            Issuer = WebhookAuthentication.Issuer,
            Audience = channelName,
            Expires = DateTime.UtcNow.Add(DeliveryTokenLifetime),
            Claims = new Dictionary<string, object>
            {
                [WebhookAuthentication.BodyHashClaim] = Base64UrlEncoder.Encode(SHA256.HashData(deliveryBody))
            },
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(channelKey)), SecurityAlgorithms.HmacSha256)
        };

        // The compact token, ready for the Authorization header.
        return tokenHandler.CreateToken(deliveryTokenDescriptor);
    }

    /// <summary>Builds one POST of the given bytes to the given address, carrying the token that vouches for them.</summary>
    private static HttpRequestMessage CreateDeliveryRequest(string deliveryUrl, byte[] deliveryBody, string deliveryToken)
    {
        // The bytes the token hashed are the bytes on the wire, declared as the JSON the channels' contract defines.
        ByteArrayContent deliveryContent = new ByteArrayContent(deliveryBody);
        deliveryContent.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        // The token rides as a bearer, the same scheme a channel uses toward Morgana.
        HttpRequestMessage deliveryRequest = new HttpRequestMessage(HttpMethod.Post, deliveryUrl) { Content = deliveryContent };
        deliveryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deliveryToken);

        // The request, sent once and disposed by the caller.
        return deliveryRequest;
    }
}