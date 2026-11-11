using System.Net;
using System.Security.Authentication;

namespace Morgana.Terminal.Services;

/// <summary>
/// Paces the attempts to open the conversation while Morgana is unreachable at startup, until the user quits.
/// It never gives up on its own: a Morgana still starting or being redeployed takes longer than any short schedule.
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

    /// <summary>The pace once the quick attempts are spent, when Morgana is down rather than slow.</summary>
    private static readonly TimeSpan SteadyRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The largest share of a delay added at random.
    /// Terminals opened against the same Morgana then do not all reach it in one burst when it comes back.
    /// </summary>
    private const double MaximumJitterShare = 0.2;

    /// <summary>The wait before the next attempt, given how many have already failed.</summary>
    public TimeSpan NextRetryDelay(int failedAttempts)
    {
        // The first attempts retry quickly and the later ones settle on the slowest delay.
        TimeSpan baseDelay = failedAttempts < QuickRetryDelays.Length
            ? QuickRetryDelays[failedAttempts]
            : SteadyRetryDelay;

        // A random share is added so channels restarted together do not all knock on Morgana at the same instant.
        return baseDelay + baseDelay * (Random.Shared.NextDouble() * MaximumJitterShare);
    }

    /// <summary>
    /// Tells whether a failed attempt is worth another: Morgana unreachable, timing out or failing on its side.
    /// A refusal such as a rejected key or an untrusted certificate would fail the same way forever, so it is reported at once.
    /// </summary>
    public static bool IsWorthRetrying(Exception exception) => exception switch
    {
        // A TLS handshake that failed is a certificate this machine does not trust.
        HttpRequestException { StatusCode: null, InnerException: AuthenticationException } => false,
        // No answer at all is a Morgana not listening yet.
        HttpRequestException { StatusCode: null } => true,
        // An answer is worth retrying only when Morgana failed on its own side.
        HttpRequestException { StatusCode: { } statusCode } => statusCode >= HttpStatusCode.InternalServerError,
        // A call that timed out met a Morgana too busy or too slow to answer yet.
        TaskCanceledException => true,
        // Anything else is a fault that another attempt would meet again.
        _ => false
    };
}