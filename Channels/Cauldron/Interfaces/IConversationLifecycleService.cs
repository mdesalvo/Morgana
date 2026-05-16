namespace Cauldron.Interfaces;

/// <summary>
/// Service for managing conversation lifecycle: start, resume, clear, and message sending.
/// Coordinates between HTTP API, SignalR groups, and local storage.
/// </summary>
public interface IConversationLifecycleService
{
    /// <summary>
    /// Starts a new conversation with Morgana backend.
    /// </summary>
    /// <returns>True if conversation started successfully.</returns>
    Task<bool> StartConversationAsync();

    /// <summary>
    /// Resumes an existing conversation from storage.
    /// Falls back to StartConversationAsync on any failure.
    /// </summary>
    /// <param name="savedConversationId">The conversation ID retrieved from storage.</param>
    /// <returns>True if conversation was resumed or a new one started successfully.</returns>
    Task<bool> ResumeConversationAsync(string savedConversationId);

    /// <summary>
    /// Clears the saved conversation from storage.
    /// </summary>
    Task ClearConversationAsync();

    /// <summary>
    /// Checks storage for an existing conversation ID.
    /// </summary>
    Task<string?> GetSavedConversationIdAsync();

    /// <summary>
    /// Sends a user message to the Morgana backend.
    /// </summary>
    /// <returns>True if the message was sent successfully.</returns>
    Task<bool> SendMessageAsync(string text);
}