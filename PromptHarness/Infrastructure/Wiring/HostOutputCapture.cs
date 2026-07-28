using System.Text;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>
/// Tees <see cref="Console.Out"/> into an in-memory ring of lines so the harness can read the
/// host's own log stream while it runs in the same process.
/// </summary>
/// <remarks>
/// <para>This is how the suite observes the <em>names</em> of the context variables an agent reads
/// and writes. Those names are not — and must not become — span attributes: span attributes reach
/// every configured exporter, and a variable name is exactly the place where a misbehaving model
/// would leak user-supplied text. The log line, which already exists for diagnostics and stays
/// inside the process, is the honest place to read them from.</para>
///
/// <para>Installation must happen <strong>before</strong> the host builds its logging stack: the
/// console logger captures <c>Console.Out</c> once, when its provider is constructed.</para>
///
/// <para>Correlation is by time window, not by conversation id — the tool log lines carry no
/// conversation id. That is sound only because the suite runs strictly serially; see
/// <c>AssemblyInfo.cs</c>, which disables xUnit parallelisation for this very reason.</para>
/// </remarks>
public sealed class HostOutputCapture : TextWriter
{
    /// <summary>The writer that was installed before the capture took over; every write is forwarded to it when echoing.</summary>
    private readonly TextWriter inner;

    /// <summary>Whether writes are forwarded to the original console (noisy, opt-in via <c>Harness:EchoHostOutput</c>).</summary>
    private readonly bool echo;

    /// <summary>Completed lines, in arrival order. Guarded by <see cref="gate"/>.</summary>
    private readonly List<string> lines = [];

    /// <summary>Partial line being accumulated between newline characters. Guarded by <see cref="gate"/>.</summary>
    private readonly StringBuilder pending = new StringBuilder();

    /// <summary>Single lock protecting <see cref="lines"/> and <see cref="pending"/>.</summary>
    private readonly Lock gate = new Lock();

    private HostOutputCapture(TextWriter inner, bool echo)
    {
        this.inner = inner;
        this.echo = echo;
    }

    /// <inheritdoc />
    public override Encoding Encoding => inner.Encoding;

    /// <summary>Replaces <see cref="Console.Out"/> with a tee and returns it.</summary>
    public static HostOutputCapture Install(bool echo)
    {
        // Wrap whatever Console.Out currently is (so echo can still forward to the real console)
        // before replacing it; TextWriter.Synchronized wraps the capture itself so writes from the
        // host's own multithreaded logging pipeline don't tear a line in half mid-write.
        HostOutputCapture capture = new HostOutputCapture(Console.Out, echo);
        Console.SetOut(TextWriter.Synchronized(capture));
        return capture;
    }

    /// <inheritdoc />
    public override void Write(char value)
    {
        if (echo)
            inner.Write(value);

        // Everything TextWriter offers ultimately funnels through this one overload (see the
        // string overload below), so the line-accumulation logic only has to live in one place.
        lock (gate)
        {
            if (value == '\n')
            {
                // TrimEnd('\r') handles the host's line endings being \r\n: the \r arrives as its
                // own character right before this \n and would otherwise be captured as part of
                // the line, which the regex-based parsing downstream does not expect.
                lines.Add(pending.ToString().TrimEnd('\r'));
                pending.Clear();
            }
            else
            {
                pending.Append(value);
            }
        }
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        if (value is null)
            return;

        // Delegates character-by-character to the overload above rather than duplicating the
        // line-splitting logic — slower, but this is diagnostic output, not a hot path.
        foreach (char character in value)
            Write(character);
    }

    /// <summary>Index of the next line to be captured; the harness marks it before a turn and reads the tail after.</summary>
    public int Mark()
    {
        // The count itself is the mark: Since(mark) below simply skips everything before this
        // index once more lines have been appended, so no separate cursor object is needed.
        lock (gate)
            return lines.Count;
    }

    /// <summary>Returns the lines captured since <paramref name="mark"/>.</summary>
    public IReadOnlyList<string> Since(int mark)
    {
        // Guards against a mark taken before the buffer had grown to that size — cannot normally
        // happen (marks only ever come from this same growing list), but returning empty rather
        // than throwing on an out-of-range slice keeps a defensive caller from crashing on it.
        lock (gate)
            return mark >= lines.Count ? [] : lines[mark..];
    }
}
