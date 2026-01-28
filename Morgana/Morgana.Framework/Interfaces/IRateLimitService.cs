namespace Morgana.Framework.Interfaces;

/// <summary>
/// Service for enforcing rate limits on conversation messages to prevent abuse and control costs.
/// Implements sliding window rate limiting with configurable thresholds per time window.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>Protects the system from:</para>
/// <list type="bullet">
/// <item>Accidental spam (user clicking send repeatedly)</item>
/// <item>Malicious abuse (DoS attempts)</item>
/// <item>Cost explosion (excessive LLM API calls)</item>
/// </list>
/// <para><strong>Rate Limit Strategy:</strong></para>
/// <para>Uses sliding window algorithm tracking requests in multiple time windows:
/// - Per minute (prevents burst spam)
/// - Per hour (prevents sustained abuse)
/// - Per day (enforces daily quotas)</para>
/// <para><strong>Configuration Example:</strong></para>
/// <code>
/// // appsettings.json
/// {
///   "Morgana": {
///     "RateLimiting": {
///       "Enabled": true,
///       "MaxMessagesPerMinute": 5,
///       "MaxMessagesPerHour": 30,
///       "MaxMessagesPerDay": 100
///     }
///   }
/// }
/// </code>
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
    /// - ViolatedLimit: which limit was exceeded (if any)
    /// - RetryAfterSeconds: suggested wait time before retrying
    /// </returns>
    /// <remarks>
    /// <para><strong>Atomic Operation:</strong></para>
    /// <para>This method both checks AND records in a single operation to prevent race conditions.
    /// If allowed, the request timestamp is immediately recorded.</para>
    /// <para><strong>Return Values:</strong></para>
    /// <list type="bullet">
    /// <item>IsAllowed=true → Request proceeds, timestamp recorded</item>
    /// <item>IsAllowed=false → Request denied, ViolatedLimit specifies which threshold</item>
    /// </list>
    /// </remarks>
    Task<Records.RateLimitResult> CheckAndRecordAsync(string conversationId);

    /// <summary>
    /// Resets rate limit counters for a conversation (admin/testing use).
    /// </summary>
    /// <param name="conversationId">Conversation to reset</param>
    Task ResetAsync(string conversationId);
}