namespace Cauldron.Interfaces;

/// <summary>
/// Service for managing conversation ID persistence across browser sessions.
/// Abstracts the storage mechanism (ProtectedLocalStorage) for conversation state tracking.
/// </summary>
public interface IConversationStorageService
{
    /// <summary>
    /// Retrieves the saved conversation ID from protected browser storage.
    /// </summary>
    /// <returns>
    /// The conversation ID if found and successfully decrypted; otherwise, null.
    /// </returns>
    Task<string?> GetConversationIdAsync();

    /// <summary>
    /// Saves the conversation ID to protected browser storage with automatic encryption.
    /// </summary>
    /// <param name="conversationId">Unique conversation identifier to persist</param>
    Task SaveConversationIdAsync(string conversationId);

    /// <summary>
    /// Clears the saved conversation ID from protected browser storage.
    /// </summary>
    Task ClearConversationIdAsync();
}