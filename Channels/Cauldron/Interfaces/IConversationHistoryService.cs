using Morgana.Contracts;

namespace Cauldron.Interfaces;

/// <summary>
/// Retrieves persisted conversation history so a resumed session can be rebuilt in the UI.
/// </summary>
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