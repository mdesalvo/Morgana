using Grimoire.Services;

namespace Grimoire.Interfaces;

/// <summary>
/// Abstraction over the terminal-resize notification so <see cref="ConsoleUiService"/> can
/// stay platform-agnostic. The concrete strategy is picked at startup in
/// <c>Program.cs</c> based on <see cref="OperatingSystem"/>: SIGWINCH on POSIX,
/// polling on Windows. See <see cref="SigWinchResizeWatcherService"/> and
/// <see cref="PollingResizeWatcherService"/>.
/// </summary>
public interface IViewportResizeWatcher
{
    /// <summary>
    /// Registers <paramref name="onResize"/> to be invoked whenever the terminal
    /// viewport size changes. The callback runs on a background thread; the
    /// subscriber owns any synchronisation it needs. Dispose the returned handle
    /// to stop receiving notifications — typically when the UI loop exits.
    /// </summary>
    IDisposable Subscribe(Action onResize);
}
