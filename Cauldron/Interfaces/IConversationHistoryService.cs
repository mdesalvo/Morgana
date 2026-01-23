using Cauldron.Messages;

namespace Cauldron.Interfaces;

/// <summary>
/// Service for retrieving conversation message history from backend.
/// Provides HTTP-based access to persisted conversation state for UI rendering.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service abstracts the HTTP call to GET /api/conversation/{id}/history,
/// enabling the Cauldron frontend to load complete conversation history when resuming
/// a conversation from ProtectedLocalStorage.</para>
/// <para><strong>Usage Pattern:</strong></para>
/// <code>
/// // In Index.razor - ResumeConversationAsync
/// await SignalRService.JoinConversation(conversationId);
///
/// // Load history via HTTP (not SignalR)
/// ConversationHistoryResponse? history = await HistoryService.GetHistoryAsync(conversationId);
/// if (history?.Messages != null)
/// {
///     foreach (var msg in history.Messages)
///     {
///         messages.Add(MapToChatMessage(msg));
///     }
/// }
///
/// // Remove loader, display history
/// StateHasChanged();
/// </code>
/// </remarks>
public interface IConversationHistoryService
{
    /// <summary>
    /// Retrieves the complete conversation history for a given conversation ID.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation to retrieve</param>
    /// <returns>
    /// ConversationHistoryResponse with messages array if successful; otherwise, null.
    /// Returns null on 404 (conversation not found) or network errors.
    /// </returns>
    Task<ConversationHistoryResponse?> GetHistoryAsync(string conversationId);
}