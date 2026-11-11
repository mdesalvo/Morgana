namespace Morgana.AI.Interfaces;

/// <summary>Bounds how often a conversation may call, over sliding windows of a minute, an hour and a day.</summary>
// Fails open: a fault of the log never blocks the user.
public interface IRateLimitService
{
    /// <summary>
    /// Tells whether the conversation may make this call and records it when it may.
    /// Checks and records in one step, so two concurrent calls cannot both pass on the same free slot.
    /// </summary>
    /// <returns>The verdict and, on a refusal, the window and cap that were exceeded with the seconds to wait.</returns>
    Task<Records.RateLimitResult> CheckAndRecordAsync(string conversationId);
}