using Microsoft.Extensions.Configuration;

namespace PromptHarness.Infrastructure;

/// <summary>
/// Knobs of the harness itself, read from <c>appsettings.Harness.json</c> (section <c>Harness</c>).
/// </summary>
/// <remarks>
/// These are deliberately separate from the <c>Morgana:</c> configuration tree: everything under
/// <c>Morgana:</c> describes the instance under test and is inherited verbatim from
/// <c>Morgana.Web</c>'s own configuration, while everything here describes how the suite drives it.
/// </remarks>
public sealed class HarnessOptions
{
    /// <summary>
    /// Whether the guard rail stays enabled on the instance under test. Off by default: no scenario
    /// asserts moderation behaviour, and every guarded turn costs one extra LLM round trip.
    /// </summary>
    public bool EnableGuardrail { get; init; }

    /// <summary>Seconds to wait for the host to answer <c>GET /api/morgana/health</c> before giving up.</summary>
    public int StartupTimeoutSeconds { get; init; } = 180;

    /// <summary>Seconds to wait for the webhook delivery of a turn's final message.</summary>
    public int TurnTimeoutSeconds { get; init; } = 180;

    /// <summary>
    /// Milliseconds to wait after a turn's final message before reading the captured host log.
    /// The console logger writes on a background queue, so tool log lines can trail the webhook
    /// delivery by a few milliseconds.
    /// </summary>
    public int LogDrainMilliseconds { get; init; } = 400;

    /// <summary>Default number of runs per scenario when the scenario does not override it.</summary>
    public int DefaultRuns { get; init; } = 5;

    /// <summary>Default number of passing runs required when the scenario does not override it.</summary>
    public int DefaultMinPasses { get; init; } = 4;

    /// <summary>Whether the host's stdout is echoed to the real console (noisy; useful when diagnosing).</summary>
    public bool EchoHostOutput { get; init; }

    /// <summary>Minimum level for the <c>Morgana</c> log categories on the host under test.</summary>
    public string HostLogLevel { get; init; } = "Information";

    /// <summary>
    /// Name of the revision phase this run measures — <c>v0</c> for the original assessment, then
    /// <c>A2.1</c>, <c>A2.2</c> and so on. It is the row key in every baseline file.
    /// </summary>
    /// <remarks>
    /// Bump it in <c>appsettings.Harness.json</c> when starting a new phase, or override it for a single
    /// run with <c>Harness__Phase</c>. Re-running the same phase replaces its row rather than appending
    /// another: a phase is a state of the prose, not a count of how many times it was measured.
    /// </remarks>
    public string Phase { get; init; } = "v0";

    /// <summary>
    /// Where the journey files and failure reports are written. An absolute path is used as-is; a
    /// relative one resolves against the project directory, not the build output.
    /// </summary>
    /// <remarks>
    /// Deliberately not the repository: the baseline is a local measurement log, not a build
    /// artefact meant for review. Point it outside the checkout (a shared drive, a per-machine
    /// results folder) if several people run the suite and should not overwrite each other's rows.
    /// </remarks>
    public string BaselineDirectory { get; init; } = "Baseline";

    /// <summary>Binds the <c>Harness</c> section, falling back to the defaults above when absent.</summary>
    public static HarnessOptions Load(IConfiguration configuration)
        // GetSection never returns null, but Get<T>() does when the section has no children at
        // all (e.g. appsettings.Harness.json is missing the "Harness" object entirely) — the
        // null-coalesce is what makes every property default rather than throwing on first use.
        => configuration.GetSection("Harness").Get<HarnessOptions>() ?? new HarnessOptions();
}
