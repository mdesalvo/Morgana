using Morgana.Terminal.Interfaces;

namespace Morgana.Terminal.Services;

/// <summary>
/// Windows fallback of <see cref="IViewportResizeWatcher"/>: there is no SIGWINCH equivalent at this layer, so a
/// low-frequency task compares the window size with the last one seen and calls back only on change.
/// </summary>
/// <remarks>The 250 ms cadence is well below human resize speed and well above the cost of two console reads, so idle CPU stays negligible.</remarks>
public sealed class PollingResizeWatcherService : IViewportResizeWatcher
{
    /// <summary>Cadence of the size comparison.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public IDisposable Subscribe(Action onResize)
    {
        // Disposing the subscription cancels this source
        CancellationTokenSource pollingSource = new();

        // The task runs on its own for as long as the subscription lives: disposing it cancels the token, which
        // unblocks the delay and lets the task end
        _ = Task.Run(async () =>
        {
            // The size the window had when the subscription started is what the first change is measured against
            int lastWidth = Console.WindowWidth;
            int lastHeight = Console.WindowHeight;

            // The loop runs until the subscription is disposed
            while (!pollingSource.Token.IsCancellationRequested)
            {
                try
                {
                    // One poll interval passes between two comparisons
                    await Task.Delay(PollInterval, pollingSource.Token);
                }
                catch (OperationCanceledException)
                {
                    // The subscription was disposed: the task ends
                    return;
                }

                // The size is read inside a guard because the terminal may vanish between polls
                int width;
                int height;
                try
                {
                    // Both dimensions are read in the same moment
                    width = Console.WindowWidth;
                    height = Console.WindowHeight;
                }
                catch (IOException)
                {
                    // The terminal disconnected mid-poll: this tick is skipped, since the host tears the UI
                    // down through the SIGHUP/SIGTERM path moments later
                    continue;
                }

                // Only a change of size is worth a repaint
                if (width != lastWidth || height != lastHeight)
                {
                    // The new size is the one the next change is measured against
                    lastWidth = width;
                    lastHeight = height;
                    onResize();
                }
            }
        }, pollingSource.Token);

        // The caller owns the polling from here
        return new Subscription(pollingSource);
    }

    /// <summary>Handle that stops the polling loop when disposed.</summary>
    private sealed class Subscription(CancellationTokenSource pollingSource) : IDisposable
    {
        /// <summary>Cancels the loop and releases its token source.</summary>
        public void Dispose()
        {
            // Disposing stops the loop
            pollingSource.Cancel();
            pollingSource.Dispose();
        }
    }
}
