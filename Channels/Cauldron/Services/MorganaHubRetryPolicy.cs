using Microsoft.AspNetCore.SignalR.Client;

namespace Cauldron.Services;

/// <summary>
/// Paces the attempts to reach Morgana's hub, both when the first connection fails and when an
/// established one drops. It never gives up: a circuit can stay open for hours (the widget's iframe
/// is never torn down) and a Morgana redeploy lasts longer than any short schedule, so the attempts
/// end only when the circuit stops the connection.
/// </summary>
public sealed class MorganaHubRetryPolicy : IRetryPolicy
{
    /// <summary>
    /// The opening attempts, close together so that a network blip recovers within seconds.
    /// </summary>
    private static readonly TimeSpan[] QuickRetryDelays =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    /// <summary>
    /// The pace once the quick attempts are spent: Morgana is down rather than unlucky.
    /// </summary>
    private static readonly TimeSpan SteadyRetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The largest share of a delay added at random. Every open circuit loses Morgana at the same
    /// moment, so without it they would all hit the instance that has just come back in one burst.
    /// </summary>
    private const double MaximumJitterShare = 0.2;

    /// <summary>
    /// Returns the wait before the next attempt, never null: the connection is retried for as long
    /// as the circuit keeps it.
    /// </summary>
    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        TimeSpan baseDelay = retryContext.PreviousRetryCount < QuickRetryDelays.Length
            ? QuickRetryDelays[retryContext.PreviousRetryCount]
            : SteadyRetryDelay;

        return baseDelay + baseDelay * (Random.Shared.NextDouble() * MaximumJitterShare);
    }
}
