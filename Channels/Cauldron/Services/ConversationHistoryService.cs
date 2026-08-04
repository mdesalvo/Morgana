using Cauldron.Interfaces;
using Morgana.Contracts;

namespace Cauldron.Services;

/// <summary>
/// Fetches a conversation's message history over HTTP. Deliberately not SignalR: the caller needs
/// the whole history in one awaited call, so it can hold the loader up until the chat is ready to
/// render. Every failure returns null and leaves the recovery decision to the caller.
/// </summary>
public class ConversationHistoryService : IConversationHistoryService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public ConversationHistoryService(
        HttpClient httpClient,
        ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ConversationHistoryResponse?> GetHistoryAsync(string conversationId)
    {
        try
        {
            _logger.LogInformation("Retrieving history for conversation {ConversationId}", conversationId);

            HttpResponseMessage response = await _httpClient.GetAsync(
                $"/api/morgana/conversation/{conversationId}/history");

            if (response.IsSuccessStatusCode)
            {
                ConversationHistoryResponse? history = await response.Content
                    .ReadFromJsonAsync<ConversationHistoryResponse>();

                _logger.LogInformation(
                    "Retrieved {MessagesLength} messages for conversation {ConversationId}", history?.Messages?.Length ?? 0, conversationId);

                return history;
            }

            // Expected whenever a saved id outlives the conversation behind it; null lets the
            // caller fall back to a fresh conversation rather than treating it as an error.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Conversation {ConversationId} not found (404)", conversationId);
                return null;
            }

            // Any other status: read the body purely so the failure is diagnosable from the log
            string errorContent = await response.Content.ReadAsStringAsync();
            _logger.LogError(
                "Failed to retrieve history for {ConversationId}: {ResponseStatusCode} - {ErrorContent}", conversationId, response.StatusCode, errorContent);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception retrieving history for conversation {ConversationId}", conversationId);
            return null;
        }
    }
}