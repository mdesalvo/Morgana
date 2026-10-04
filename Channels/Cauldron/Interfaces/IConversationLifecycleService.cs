using Morgana.Contracts;

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
    /// Raised, from a timer thread, when a turn in flight has gone silent for longer than
    /// <c>Cauldron:ReplyTimeoutSeconds</c>. Nothing on screen has changed yet: the subscriber, which
    /// alone runs on the circuit, settles the turn by recovering its reply or abandoning it.
    /// </summary>
    event Action? OnReplyDeadlineExpired;

    /// <summary>True while a sent turn is waiting on Morgana's reply.</summary>
    bool IsAwaitingReply { get; }

    /// <summary>
    /// Records that Morgana is answering — a chunk or a message — so the turn in flight keeps its
    /// place. A turn that has already been answered or given up on is unaffected.
    /// </summary>
    void NoteReplyActivity();

    /// <summary>
    /// Asks Morgana for the replies it wrote to this conversation past the latest one this client
    /// was given, as the messages a push would have carried. Empty when nothing is waiting, when
    /// nothing is missing or when Morgana cannot be asked right now.
    /// </summary>
    Task<IReadOnlyList<ChannelMessage>> GetMissedRepliesAsync();

    /// <summary>
    /// Gives up on the turn in flight: what a stream had revealed stays as the partial reply it is,
    /// the composer is freed and the conversation says plainly that the answer may never come.
    /// </summary>
    void AbandonTurn();
}