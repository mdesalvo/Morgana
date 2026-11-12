using Morgana.Contracts;

namespace Cauldron.Interfaces;

/// <summary>
/// Starts, resumes and ends the visitor's conversation with Morgana and sends its messages.
/// It keeps the SignalR group, the stored id and the reply deadline in step.
/// </summary>
public interface IConversationLifecycleService
{
    /// <summary>Starts a new conversation, joining its group first so the welcome cannot be lost; true when it started.</summary>
    Task<bool> StartConversationAsync();

    /// <summary>Resumes the conversation the browser kept, starting a new one on any failure; true when a conversation is on screen.</summary>
    Task<bool> ResumeConversationAsync(string savedConversationId);

    /// <summary>Ends the current conversation on Morgana's side, which stops its actors and keeps its record.</summary>
    Task EndConversationAsync();

    /// <summary>Forgets the conversation the browser kept, so the next load starts a new one.</summary>
    Task ClearConversationAsync();

    /// <summary>Returns the conversation id the browser kept; null for a first visit.</summary>
    Task<string?> GetSavedConversationIdAsync();

    /// <summary>Sends the visitor's message to Morgana and arms the reply deadline; true when Morgana took it.</summary>
    Task<bool> SendMessageAsync(string text);

    /// <summary>
    /// Raised, from a timer thread, when a turn in flight has gone silent for longer than the wait that Morgana reports.
    /// Nothing on screen has changed yet: the subscriber, which alone runs on the circuit, settles the turn by recovering
    /// its reply or abandoning it.
    /// </summary>
    event Action? OnReplyDeadlineExpired;

    /// <summary>True while a sent turn is waiting on Morgana's reply.</summary>
    bool IsAwaitingReply { get; }

    /// <summary>
    /// Records that Morgana is answering, with a chunk or a message, so the turn in flight keeps its place.
    /// A turn that has already been answered or given up on is unaffected.
    /// </summary>
    void NoteReplyActivity();

    /// <summary>
    /// Asks Morgana for the replies it wrote to this conversation past the latest one this client was given, as the messages a push would have carried.
    /// Empty when nothing is waiting, when nothing is missing or when Morgana cannot be asked right now.
    /// </summary>
    Task<IReadOnlyList<ChannelMessage>> GetMissedRepliesAsync();

    /// <summary>Gives up on the turn in flight: what a stream had revealed stays as the partial reply it is, the composer is freed and the conversation says plainly that the answer may never come.</summary>
    void AbandonTurn();
}