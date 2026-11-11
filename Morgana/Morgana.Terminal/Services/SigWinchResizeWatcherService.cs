using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Morgana.Terminal.Interfaces;

namespace Morgana.Terminal.Services;

/// <summary>
/// Tells the console UI of a resize on POSIX through SIGWINCH, the signal the kernel sends the foreground process group whenever its terminal is resized.
/// One callback per resize and no idle CPU, unlike polling. Windows has no such signal, so TerminalChannelExtensions registers this service on POSIX only.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class SigWinchResizeWatcherService : IViewportResizeWatcher
{
    /// <inheritdoc />
    public IDisposable Subscribe(Action onResize)
    {
        // The registration unhooks the signal when disposed, which is the lifetime the interface promises the caller.
        return PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ => onResize());
    }
}