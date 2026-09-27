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
    /// <param name="seal">The conversation's seal, without which Morgana answers as if it did not exist</param>
    /// <returns>
    /// ConversationHistoryResponse with messages array if successful; otherwise, null.
    /// Returns null on 404 (conversation not found or not sealed with that seal) or network errors.
    /// </returns>
    Task<ConversationHistoryResponse?> GetHistoryAsync(string conversationId, string seal);
}