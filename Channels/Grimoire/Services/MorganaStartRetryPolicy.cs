using System.Net;
using System.Security.Authentication;

namespace Grimoire.Services;

/// <summary>
/// Paces the attempts to open the conversation while Morgana is unreachable at startup. It never gives
/// up on its own: a Morgana still starting or being redeployed takes longer than any short schedule,
/// so the attempts end only when the user quits.
/// </summary>
public sealed class MorganaStartRetryPolicy
{
    /// <summary>The opening attempts, close together so that a Morgana a few seconds from ready is met at once.</summary>
    private static readonly TimeSpan[] QuickRetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    /// <summary>The pace once the quick attempts are spent: Morgana is down rather than slow.</summary>
    private static readonly TimeSpan SteadyRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The largest share of a delay added at random, so that terminals opened against the same Morgana
    /// do not all reach it in one burst when it comes back.
    /// </summary>
    private const double MaximumJitterShare = 0.2;

    /// <summary>Returns the wait before the next attempt, given how many have already failed.</summary>
    public TimeSpan NextRetryDelay(int failedAttempts)
    {
        TimeSpan baseDelay = failedAttempts < QuickRetryDelays.Length
            ? QuickRetryDelays[failedAttempts]
            : SteadyRetryDelay;

        return baseDelay + baseDelay * (Random.Shared.NextDouble() * MaximumJitterShare);
    }

    /// <summary>
    /// Tells whether a failed attempt is worth another: Morgana unreachable, timing out or failing on its
    /// side. A refusal such as a rejected key or an untrusted certificate would fail the same way forever,
    /// so it is reported at once.
    /// </summary>
    public static bool IsWorthRetrying(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: null, InnerException: AuthenticationException } => false,
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } statusCode } => statusCode >= HttpStatusCode.InternalServerError,
        TaskCanceledException => true,
        _ => false
    };
}
