using Grimoire.Interfaces;

namespace Grimoire.Services;

/// <summary>
/// Windows fallback implementation of <see cref="IViewportResizeWatcher"/>:
/// .NET does not surface a SIGWINCH-equivalent event on Windows at this layer
/// (the raw console-buffer events would require interop with kernel32), so we
/// spin a low-frequency background task that compares <see cref="Console.WindowWidth"/>
/// and <see cref="Console.WindowHeight"/> against the last seen values and fires
/// the callback only on change.
/// </summary>
/// <remarks>
/// The 250 ms cadence is well below human resize speed (drag operations produce
/// many size changes per second, the user only perceives the final state) and
/// well above the cost of two console syscalls, keeping idle CPU negligible.
/// </remarks>
public sealed class PollingResizeWatcherService : IViewportResizeWatcher
{
    /// <summary>Cadence of the size-comparison tick — see class remarks for the rationale.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    /// <inheritdoc />
    public IDisposable Subscribe(Action onResize)
    {
        CancellationTokenSource cts = new();

        // Fire-and-forget polling task; the returned Subscription cancels the CTS
        // on dispose, which unblocks Task.Delay and lets the task exit cleanly.
        _ = Task.Run(async () =>
        {
            int lastW = Console.WindowWidth;
            int lastH = Console.WindowHeight;

            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(PollInterval, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                int width;
                int height;
                try
                {
                    width = Console.WindowWidth;
                    height = Console.WindowHeight;
                }
                catch (IOException)
                {
                    // Terminal disconnected mid-poll — skip this tick. The host
                    // lifetime will tear the UI down momentarily through the
                    // standard SIGHUP/SIGTERM path; no need to surface this here.
                    continue;
                }

                if (width != lastW || height != lastH)
                {
                    lastW = width;
                    lastH = height;
                    onResize();
                }
            }
        }, cts.Token);

        return new Subscription(cts);
    }

    /// <summary>Disposable handle that cancels the polling loop on <see cref="Dispose"/>.</summary>
    private sealed class Subscription(CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}
