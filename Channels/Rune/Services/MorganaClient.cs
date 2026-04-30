using Rune.Messages;
using Rune.Messages.Contracts;

namespace Rune.Services;

/// <summary>
/// Thin REST client wrapping the Morgana conversation lifecycle endpoints
/// (<c>/api/morgana/conversation/start</c>, <c>.../message</c>, <c>.../end</c>).
/// Relies on <see cref="IHttpClientFactory"/>'s named <c>Morgana</c> client, which
/// is wired with the per-issuer JWT <see cref="Handlers.MorganaAuthHandler"/>.
/// </summary>
public sealed class MorganaClient
{
    /// <summary>Fallback cap advertised at the handshake when <c>Rune:MaxMessageLength</c> is absent.</summary>
    private const int DefaultMaxMessageLength = 500;

    /// <summary>Produces the named <c>Morgana</c> <see cref="HttpClient"/> with the JWT handler already wired in.</summary>
    private readonly IHttpClientFactory httpClientFactory;

    /// <summary>Absolute URL Morgana POSTs inbound messages to; re-announced on every handshake.</summary>
    private readonly string callbackUrl;

    /// <summary>Hard cap Rune advertises to Morgana's channel adapter; <c>null</c> means "no cap".</summary>
    private readonly int? maxMessageLength;

    /// <summary>Captures the callback URL (required) and the advertised message length cap.</summary>
    /// <exception cref="InvalidOperationException">Thrown when <c>Rune:CallbackURL</c> is missing.</exception>
    public MorganaClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        this.httpClientFactory = httpClientFactory;
        callbackUrl = configuration["Rune:CallbackURL"]
            ?? throw new InvalidOperationException("Rune:CallbackURL is required for webhook-based delivery.");

        // Rune:MaxMessageLength governs the hard cap Rune announces to Morgana at the
        // handshake. Default (500) stays aggressive so the downgrade path is exercised on
        // every turn — the "poor but honest" profile Rune was built for — but can be raised
        // (or set to null to mean "no cap") without recompiling.
        maxMessageLength = configuration.GetValue<int?>("Rune:MaxMessageLength") ?? DefaultMaxMessageLength;
    }

    /// <summary>
    /// Opens a new conversation with Morgana, declaring Rune's handshake
    /// (<c>channelName=rune</c>, <c>deliveryMode=webhook</c>, capabilities off, callback URL).
    /// </summary>
    public async Task<string> StartConversationAsync(CancellationToken cancellationToken = default)
    {
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");

        // We mint a candidate id ("N" = 32-char hex, no dashes — matches Morgana's
        // conversation id shape), but the server is source of truth: whatever it returns
        // on the response is what Rune will use from this point on.
        StartConversationRequest body = new()
        {
            ConversationId = Guid.NewGuid().ToString("N"),
            ChannelMetadata = ChannelMetadata.Build(callbackUrl, maxMessageLength)
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
