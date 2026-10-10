namespace Morgana.AI.Interfaces;

/// <summary>
/// Bounds how often a conversation may send messages, over sliding windows with configurable thresholds.
/// </summary>
/// <remarks>
/// A conversation is limited per minute (burst spam), per hour (sustained abuse) and per day (quota). The limiter protects
/// the system from accidental spam, deliberate abuse and runaway LLM cost.
/// </remarks>
public interface IRateLimitService
{
    /// <summary>
    /// Checks if a request is allowed under current rate limits.
    /// Also records the request if allowed for future limit checks.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation</param>
    /// <returns>
    /// RateLimitResult containing:
    /// - IsAllowed: true if request should proceed
    /// - ViolatedWindow and ViolatedCap: which limit was exceeded (if any)
    /// - RetryAfterSeconds: suggested wait time before retrying
    /// </returns>
    /// <remarks>
    /// Checking and recording are one atomic step so that concurrent requests cannot both slip under a limit.
    /// A denied request is not recorded.
    /// </remarks>
    Task<Records.RateLimitResult> CheckAndRecordAsync(string conversationId);

    /// <summary>
    /// Resets rate limit counters for a conversation (admin/testing use).
    /// </summary>
    /// <param name="conversationId">Conversation to reset</param>
    Task ResetAsync(string conversationId);
}