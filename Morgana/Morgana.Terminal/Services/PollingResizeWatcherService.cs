using Morgana.Terminal.Interfaces;

namespace Morgana.Terminal.Services;

/// <summary>
/// Tells the console UI of a resize on Windows by comparing the window's size with the last one seen.
/// Windows has no SIGWINCH at this layer. Every 250 ms is well below human resize speed and well above the cost of two
/// console reads, so idle CPU stays negligible.
/// </summary>
public sealed class PollingResizeWatcherService : IViewportResizeWatcher
{
    /// <summary>The cadence of the size comparison.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public IDisposable Subscribe(Action onResize)
    {
        // Disposing the subscription cancels this source.
        CancellationTokenSource pollingSource = new();

        // The task runs for as long as the subscription lives: disposing it cancels the token, which unblocks the delay and ends the task.
        _ = Task.Run(async () =>
        {
            // The size the window had when the subscription started is what the first change is measured against.
            int lastWidth = Console.WindowWidth;
            int lastHeight = Console.WindowHeight;

            // The loop runs until the subscription is disposed.
            while (!pollingSource.Token.IsCancellationRequested)
            {
                try
                {
                    // One poll interval passes between two comparisons.
                    await Task.Delay(PollInterval, pollingSource.Token);
                }
                catch (OperationCanceledException)
                {
                    // The subscription was disposed, so the task ends.
                    return;
                }

                // The size is read inside a guard because the terminal may vanish between polls.
                int width;
                int height;
                try
                {
                    // Both dimensions are read in the same moment.
                    width = Console.WindowWidth;
                    height = Console.WindowHeight;
                }
                catch (IOException)
                {
                    // The terminal disconnected mid-poll: this tick is skipped, since the host tears the UI
                    // down through the SIGHUP or SIGTERM path moments later.
                    continue;
                }

                // Only a change of size is worth a repaint.
                if (width != lastWidth || height != lastHeight)
                {
                    // The new size is the one the next change is measured against.
                    lastWidth = width;
                    lastHeight = height;
                    onResize();
                }
            }
        }, pollingSource.Token);

        // The caller owns the polling from here.
        return new Subscription(pollingSource);
    }

    /// <summary>Stops the polling loop when disposed.</summary>
    private sealed class Subscription(CancellationTokenSource pollingSource) : IDisposable
    {
        /// <summary>Cancels the loop and releases its token source.</summary>
        public void Dispose()
        {
            // The loop ends at its next delay and the source holds nothing more.
            pollingSource.Cancel();
            pollingSource.Dispose();
        }
    }
}