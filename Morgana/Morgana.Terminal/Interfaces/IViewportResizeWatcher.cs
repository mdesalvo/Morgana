using Morgana.Terminal.Services;

namespace Morgana.Terminal.Interfaces;

/// <summary>Tells the console UI that the terminal was resized, whatever the platform reports it through.</summary>
// TerminalChannelExtensions picks the strategy at startup: SigWinchResizeWatcherService on POSIX and PollingResizeWatcherService on Windows.
public interface IViewportResizeWatcher
{
    /// <summary>Invokes <paramref name="onResize"/> whenever the terminal's size changes, until the returned handle is disposed.</summary>
    // The callback runs on a background thread, so the subscriber owns any synchronisation it needs.
    IDisposable Subscribe(Action onResize);
}