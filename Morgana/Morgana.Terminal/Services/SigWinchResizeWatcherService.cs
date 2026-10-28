using System.Runtime.InteropServices;
using Morgana.Terminal.Interfaces;

namespace Morgana.Terminal.Services;

/// <summary>
/// POSIX implementation of <see cref="IViewportResizeWatcher"/>: hooks SIGWINCH,
/// the signal the kernel delivers to the foreground process group every time the
/// controlling terminal is resized. One
/// callback per resize event and no idle CPU.
/// </summary>
/// <remarks>
/// <see cref="PosixSignal"/> does not expose a named constant for SIGWINCH, but
/// <see cref="PosixSignalRegistration.Create"/> accepts arbitrary positive enum
/// values and treats them as raw OS signal numbers. SIGWINCH is signal 28 on every
/// mainstream Unix (Linux on all common architectures, macOS/Darwin, FreeBSD), so
/// the cast is portable across the POSIX family the channels target.
/// </remarks>
public sealed class SigWinchResizeWatcherService : IViewportResizeWatcher
{
    /// <summary>Numeric value of SIGWINCH on Linux, macOS and BSD.</summary>
    private const int SIGWINCH = 28;

    /// <inheritdoc />
    public IDisposable Subscribe(Action onResize)
    {
        // The registration unhooks the signal when disposed, which is the lifetime the interface promises the caller
        return PosixSignalRegistration.Create((PosixSignal)SIGWINCH, _ => onResize());
    }
}
