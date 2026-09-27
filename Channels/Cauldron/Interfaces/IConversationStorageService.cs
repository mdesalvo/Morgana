using Cauldron.Messages;

namespace Cauldron.Interfaces;

/// <summary>
/// Service for managing conversation persistence across browser sessions.
/// Abstracts the storage mechanism (ProtectedLocalStorage) for conversation state tracking.
/// </summary>
public interface IConversationStorageService
{
    /// <summary>
    /// Retrieves the saved conversation with its seal from protected browser storage.
    /// </summary>
    /// <returns>
    /// The conversation if found and successfully decrypted; otherwise, null.
    /// </returns>
    Task<StoredConversation?> GetStoredConversationAsync();

    /// <summary>
    /// Saves the conversation with its seal to protected browser storage with automatic encryption.
    /// </summary>
    /// <param name="storedConversation">The conversation to resume on the next visit</param>
    Task SaveStoredConversationAsync(StoredConversation storedConversation);

    /// <summary>
    /// Clears the saved conversation from protected browser storage.
    /// </summary>
    Task ClearStoredConversationAsync();
}
