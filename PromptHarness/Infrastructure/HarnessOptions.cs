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
    /// asserts moderation behaviour and every guarded turn costs one extra LLM round trip.
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
    /// <c>A2.1</c>, <c>A2.2</c> and so on. It is the row key in every harness file.
    /// </summary>
    /// <remarks>
    /// Bump it in <c>appsettings.Harness.json</c> when starting a new phase, or override it for a single
    /// run with <c>Harness__Phase</c>. Re-running the same phase replaces its row rather than appending
    /// another: a phase is a state of the prose, not a count of how many times it was measured.
    /// </remarks>
    public string Phase { get; init; } = "v0";

    /// <summary>
    /// Overrides <c>Morgana:HistoryReducer:SummarizationThreshold</c> at boot, when set. The default
    /// (12, summing with <see cref="SummarizationTargetCount"/>'s default of 8 to a 21-message
    /// trigger) is far above what any scripted scenario reaches — deliberately unset here, so every
    /// class except <c>SummarizationTests</c> runs against the inherited, unmodified value. Lowering
    /// it is process-wide for the whole shared host, exactly like <see cref="EnableGuardrail"/>: run
    /// it in its own filtered <c>dotnet test</c> invocation, never alongside the rest of the suite.
    /// </summary>
    public int? SummarizationThreshold { get; init; }

    /// <summary>Overrides <c>Morgana:HistoryReducer:SummarizationTargetCount</c> at boot, when set. See <see cref="SummarizationThreshold"/>.</summary>
    public int? SummarizationTargetCount { get; init; }

    /// <summary>
    /// Overrides <c>Morgana:DustLimiting:BudgetPerConversation</c> at boot, when set and also
    /// switches <c>Morgana:DustLimiting:Enabled</c> on for the run — dust limiting is force-disabled
    /// otherwise (see <c>MorganaHostFixture.ApplyHostEnvironment</c>), the same reasoning as
    /// <see cref="SummarizationThreshold"/>: process-wide for the single assembly-shared host, so
    /// only <c>DustTests</c>' own filtered <c>dotnet test</c> invocation should ever set it. Pick a
    /// value small enough that a handful of cheap scripted turns cross 70%, then 90%, then exhaust
    /// it — see that class's own remarks for the calibration this depends on.
    /// </summary>
    public double? DustBudgetPerConversation { get; init; }

    /// <summary>
    /// Pins Examples' InventoryTool.GenerateSealWord() to this fixed value for the run instead of a
    /// fresh random one — see that method's own remarks. The harness DSL has no way to capture a
    /// value the model invents in one turn and replay it into a later turn's fixed <c>say:</c> text,
    /// so a scenario scripting ConfirmOrder needs the seal word pinned to something it can recite
    /// back verbatim. Republished to the in-process host as <c>Harness__DeterministicSealWord</c>,
    /// the one env var name Examples' plugin code reads — override it the same way as
    /// <see cref="Phase"/> if a scenario ever needs a different literal.
    /// </summary>
    public string DeterministicSealWord { get; init; } = "534LW03D";

    /// <summary>
    /// Where the journey files and failure reports are written. An absolute path is used as-is; a
    /// relative one resolves against the project directory, not the build output.
    /// </summary>
    /// <remarks>
    /// Deliberately not the repository: the harness is a local measurement log, not a build
    /// artefact meant for review. Point it outside the checkout (a shared drive, a per-machine
    /// results folder) if several people run the suite and should not overwrite each other's rows.
    /// </remarks>
    public string HarnessDirectory { get; init; } = "Harness";

    /// <summary>Binds the <c>Harness</c> section, falling back to the defaults above when absent.</summary>
    public static HarnessOptions Load(IConfiguration configuration)
        // GetSection never returns null, but Get<T>() does when the section has no children at
        // all (e.g. appsettings.Harness.json is missing the "Harness" object entirely) — the
        // null-coalesce is what makes every property default rather than throwing on first use.
        => configuration.GetSection("Harness").Get<HarnessOptions>() ?? new HarnessOptions();
}