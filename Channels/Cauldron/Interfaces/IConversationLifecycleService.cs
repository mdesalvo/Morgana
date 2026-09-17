namespace Cauldron.Interfaces;

/// <summary>
/// Service for managing conversation lifecycle: start, resume, clear and message sending.
/// Coordinates between HTTP API, SignalR groups and local storage.
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
    /// Ends the current conversation server-side so Morgana can tear down its actor tree.
    /// </summary>
    Task EndConversationAsync();

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

    /// <summary>
    /// Raised when a turn in flight has gone silent for longer than <c>Cauldron:ReplyTimeoutSeconds</c>
    /// and is given up on. The subscriber owns the repaint, since it alone runs on the circuit.
    /// </summary>
    event Action? OnTurnAbandoned;

    /// <summary>
    /// Records that Morgana is answering — a chunk or a message — so the turn in flight keeps its
    /// place. A turn that has already been answered or given up on is unaffected.
    /// </summary>
    void NoteReplyActivity();

    /// <summary>
    /// Recovers a reply pushed while this client was away, typically across a reconnection: the
    /// conversation is compared with what Morgana holds and anything missing is appended.
    /// Returns whether a turn that was waiting is now answered.
    /// </summary>
    Task<bool> RecoverMissedRepliesAsync();
}