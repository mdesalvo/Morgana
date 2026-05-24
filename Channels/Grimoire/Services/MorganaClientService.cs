using Grimoire.Messages;
using Grimoire.Messages.Contracts;

namespace Grimoire.Services;

/// <summary>
/// Thin REST client wrapping the Morgana conversation lifecycle endpoints
/// (<c>/api/morgana/conversation/start</c>, <c>.../message</c>, <c>.../end</c>).
/// Relies on <see cref="IHttpClientFactory"/>'s named <c>Morgana</c> client, which
/// is wired with the per-issuer JWT <see cref="Handlers.MorganaAuthHandler"/>.
/// </summary>
public sealed class MorganaClientService
{
    /// <summary>Produces the named <c>Morgana</c> <see cref="HttpClient"/> with the JWT handler already wired in.</summary>
    private readonly IHttpClientFactory httpClientFactory;

    /// <summary>Absolute URL Morgana POSTs inbound messages to; re-announced on every handshake.</summary>
    private readonly string callbackUrl;

    /// <summary>Captures the callback URL (required).</summary>
    /// <exception cref="InvalidOperationException">Thrown when <c>Grimoire:CallbackURL</c> is missing.</exception>
    public MorganaClientService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        this.httpClientFactory = httpClientFactory;
        callbackUrl = configuration["Grimoire:CallbackURL"]
            ?? throw new InvalidOperationException("Grimoire:CallbackURL is required for webhook-based delivery.");
    }

    /// <summary>
    /// Opens a new conversation with Morgana, declaring Grimoire's handshake
    /// (<c>channelName=grimoire</c>, <c>deliveryMode=webhook</c>, full TTY capability
    /// profile, callback URL).
    /// </summary>
    public async Task<string> StartConversationAsync(CancellationToken cancellationToken = default)
    {
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");

        // We mint a candidate id ("N" = 32-char hex, no dashes — matches Morgana's
        // conversation id shape), but the server is source of truth: whatever it returns
        // on the response is what Grimoire will use from this point on.
        StartConversationRequest body = new()
        {
            ConversationId = Guid.NewGuid().ToString("N"),
            ChannelMetadata = ChannelMetadata.Build(callbackUrl)
        };

        HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            "/api/morgana/conversation/start", body, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Fail-closed: a 2xx with an empty/missing id means the contract is broken —
        // refuse to run rather than leak an undefined conversation into the webhook loop.
        StartConversationResponse? parsed = await response.Content.ReadFromJsonAsync<StartConversationResponse>(cancellationToken);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.ConversationId))
            throw new InvalidOperationException("Morgana did not return a conversation id.");
        return parsed.ConversationId;
    }

    /// <summary>Sends a user message on the given conversation.</summary>
    public async Task SendMessageAsync(string conversationId, string text, CancellationToken cancellationToken = default)
    {
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");
        HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            $"/api/morgana/conversation/{conversationId}/message",
            new SendMessageRequest { ConversationId = conversationId, Text = text },
            cancellationToken);

        // 429 (rate-limit OR dust exhaustion) is not a transport failure: before returning
        // it the backend has already pushed a user-facing explanatory ChannelMessage over
        // the webhook (rendered by DrainIncomingLoop, with its own terminal styling). Letting
        // EnsureSuccessStatusCode throw here would surface a raw "send failed: 429" line that
        // buries that message and reads like a crash. Swallow it and let the channel speak —
        // same contract Cauldron honours. Any other non-success still throws so genuine
        // failures stay visible.
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            return;

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Terminates the conversation server-side; best-effort (swallows errors on shutdown).</summary>
    public async Task EndConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient httpClient = httpClientFactory.CreateClient("Morgana");
            await httpClient.PostAsync($"/api/morgana/conversation/{conversationId}/end",
                content: null, cancellationToken);
        }
        catch
        {
            // Best-effort on shutdown — if Morgana is already gone or unreachable, we don't care.
        }
    }
}
