using System.Text;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>
/// Tees <see cref="Console.Out"/> into an in-memory list of lines, so the harness reads the host's log while it runs in the same process.
/// It is how the suite sees the names of the context variables an agent touches, which never become span attributes since a misbehaving model would leak user text through them.
/// It must be installed before the host builds its logging; its time-window correlation holds only because the suite runs serially (see <c>AssemblyInfo.cs</c>).
/// </summary>
public sealed class HostOutputCapture : TextWriter
{
    /// <summary>The writer that was installed before the capture took over; every write is forwarded to it when echoing.</summary>
    private readonly TextWriter inner;

    /// <summary>Whether writes are forwarded to the original console, a noisy option that <c>Harness:EchoHostOutput</c> turns on.</summary>
    private readonly bool echo;

    /// <summary>Completed lines in arrival order, written under <see cref="gate"/>.</summary>
    private readonly List<string> lines = [];

    /// <summary>The line being accumulated until its newline, written under <see cref="gate"/>.</summary>
    private readonly StringBuilder pending = new StringBuilder();

    /// <summary>Single lock protecting <see cref="lines"/> and <see cref="pending"/>.</summary>
    private readonly Lock gate = new Lock();

    /// <summary>Wraps the console writer that was installed, echoing to it when asked.</summary>
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
        // The real console is kept for the echo, then replaced by the capture, synchronized so the host's
        // logging threads never tear a line in half.
        HostOutputCapture capture = new HostOutputCapture(Console.Out, echo);
        Console.SetOut(TextWriter.Synchronized(capture));

        // The capture the fixture marks and reads around each turn.
        return capture;
    }

    /// <inheritdoc />
    public override void Write(char value)
    {
        // A diagnosing run sees the host's output on the real console too.
        if (echo)
            inner.Write(value);

        // Every write reaches the lines through this one place, the string overload included.
        lock (gate)
        {
            // A newline completes the line being accumulated.
            if (value == '\n')
            {
                // A Windows line ending leaves its carriage return before the newline, which the parsing downstream does not expect.
                lines.Add(pending.ToString().TrimEnd('\r'));
                pending.Clear();
            }
            else
            {
                // Any other character extends the line in progress.
                pending.Append(value);
            }
        }
    }

    /// <inheritdoc />
    public override void Write(string? value)
    {
        // A null write carries no text.
        if (value is null)
            return;

        // Character by character through the one place that splits lines: the host's log is no hot path.
        foreach (char character in value)
            Write(character);
    }

    /// <summary>Index of the next line to be captured; the harness marks it before a turn and reads the tail after.</summary>
    public int Mark()
    {
        // The number of lines so far is the mark: what is appended after it is what the turn wrote.
        lock (gate)
            return lines.Count;
    }

    /// <summary>Returns the lines captured since <paramref name="mark"/>.</summary>
    public IReadOnlyList<string> Since(int mark)
    {
        // A mark at the end means nothing was written since; the lines after it otherwise.
        lock (gate)
            return mark >= lines.Count ? [] : lines[mark..];
    }
}