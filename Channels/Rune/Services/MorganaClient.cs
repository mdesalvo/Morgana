using System.Net.Http.Json;
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
    private readonly IHttpClientFactory httpClientFactory;
    private readonly string callbackUrl;

    public MorganaClient(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        this.httpClientFactory = httpClientFactory;
        callbackUrl = configuration["Rune:CallbackURL"]
            ?? throw new InvalidOperationException("Rune:CallbackURL is required for webhook-based delivery.");
    }

    /// <summary>
    /// Opens a new conversation with Morgana, declaring Rune's handshake
    /// (<c>channelName=rune</c>, <c>deliveryMode=webhook</c>, capabilities off, callback URL).
    /// </summary>
    public async Task<string> StartConversationAsync(CancellationToken cancellationToken = default)
    {
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");

        StartConversationRequest body = new()
        {
            ConversationId = Guid.NewGuid().ToString("N"),
            ChannelMetadata = ChannelMetadata.Build(callbackUrl)
        };

        HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            "/api/morgana/conversation/start", body, cancellationToken);
        response.EnsureSuccessStatusCode();

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

    private sealed class StartConversationRequest
    {
        public required string ConversationId { get; set; }
        public required ChannelMetadata ChannelMetadata { get; set; }
    }

    private sealed class StartConversationResponse
    {
        public string ConversationId { get; set; } = string.Empty;
    }

    private sealed class SendMessageRequest
    {
        public required string ConversationId { get; set; }
        public required string Text { get; set; }
    }
}
