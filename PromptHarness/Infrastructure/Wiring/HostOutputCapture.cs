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
        HostOutputCapture capture = new HostOutputCapture(Console.Out, echo);
        Console.SetOut(TextWriter.Synchronized(capture));
        return capture;
    }

    /// <inheritdoc />
    public override void Write(char value)
    {
        if (echo)
            inner.Write(value);

        lock (gate)
        {
            if (value == '\n')
            {
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

        foreach (char character in value)
            Write(character);
    }

    /// <summary>Index of the next line to be captured; the harness marks it before a turn and reads the tail after.</summary>
    public int Mark()
    {
        lock (gate)
            return lines.Count;
    }

    /// <summary>Returns the lines captured since <paramref name="mark"/>.</summary>
    public IReadOnlyList<string> Since(int mark)
    {
        lock (gate)
            return mark >= lines.Count ? [] : lines[mark..];
    }
}
