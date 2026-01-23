using Cauldron.Interfaces;
using Cauldron.Messages;

namespace Cauldron.Services;

/// <summary>
/// HTTP-based service for retrieving conversation message history from Morgana backend.
/// Implements synchronous HTTP GET to ensure complete history is loaded before UI updates.
/// </summary>
/// <remarks>
/// <para><strong>Architecture Note:</strong></para>
/// <para>Unlike real-time message delivery (which uses SignalR), history retrieval uses HTTP
/// to ensure synchronous, predictable loading behavior. This allows the UI to keep the
/// magical loader active until the complete history is loaded and rendered.</para>
/// <para><strong>Error Handling:</strong></para>
/// <list type="bullet">
/// <item>404 Not Found → Returns null (conversation expired/deleted)</item>
/// <item>500 Server Error → Returns null (backend error)</item>
/// <item>Network Error → Returns null (connection failure)</item>
/// </list>
/// </remarks>
public class MorganaConversationHistoryService : IConversationHistoryService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<MorganaConversationHistoryService> _logger;

    /// <summary>
    /// Initializes a new instance of the ConversationHistoryService.
    /// </summary>
    /// <param name="httpClient">HTTP client for API calls (injected by DI)</param>
    /// <param name="logger">Logger instance for diagnostics</param>
    public MorganaConversationHistoryService(
        HttpClient httpClient,
        ILogger<MorganaConversationHistoryService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<ConversationHistoryResponse?> GetHistoryAsync(string conversationId)
    {
        try
        {
            _logger.LogInformation($"Retrieving history for conversation {conversationId}");

            HttpResponseMessage response = await _httpClient.GetAsync(
                $"/api/conversation/{conversationId}/history");

            if (response.IsSuccessStatusCode)
            {
                ConversationHistoryResponse? history = await response.Content
                    .ReadFromJsonAsync<ConversationHistoryResponse>();

                _logger.LogInformation(
                    $"Retrieved {history?.Messages?.Length ?? 0} messages for conversation {conversationId}");

                return history;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning($"Conversation {conversationId} not found (404)");
                return null;
            }
            else
            {
                string errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError(
                    $"Failed to retrieve history for {conversationId}: {response.StatusCode} - {errorContent}");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Exception retrieving history for conversation {conversationId}");
            return null;
        }
    }
}