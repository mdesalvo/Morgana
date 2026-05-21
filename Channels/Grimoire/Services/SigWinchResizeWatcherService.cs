using System.Runtime.InteropServices;
using Grimoire.Interfaces;

namespace Grimoire.Services;

/// <summary>
/// POSIX implementation of <see cref="IViewportResizeWatcher"/>: hooks SIGWINCH,
/// the signal the kernel delivers to the foreground process group every time the
/// controlling terminal is resized. Zero polling, zero idle CPU — exactly one
/// invocation per resize event, courtesy of the OS.
/// </summary>
/// <remarks>
/// <see cref="PosixSignal"/> does not expose a named constant for SIGWINCH, but
/// <see cref="PosixSignalRegistration.Create"/> accepts arbitrary positive enum
/// values and treats them as raw OS signal numbers. SIGWINCH is signal 28 on every
/// mainstream Unix (Linux on all common architectures, macOS/Darwin, FreeBSD), so
/// the cast is portable across the POSIX family Grimoire targets.
/// </remarks>
public sealed class SigWinchResizeWatcherService : IViewportResizeWatcher
{
    /// <summary>Numeric value of SIGWINCH on Linux/macOS/BSD — see class remarks.</summary>
    private const int SIGWINCH = 28;

    /// <inheritdoc />
    public IDisposable Subscribe(Action onResize)
    {
        // PosixSignalRegistration returns an IDisposable that unregisters the
        // handler on dispose — exactly the lifecycle contract our interface
        // promises, so we just forward it to the caller.
        return PosixSignalRegistration.Create((PosixSignal)SIGWINCH, _ => onResize());
    }
}
