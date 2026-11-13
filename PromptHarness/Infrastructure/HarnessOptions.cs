using Microsoft.Extensions.Configuration;

namespace PromptHarness.Infrastructure;

/// <summary>
/// The knobs of the harness itself, read from the <c>Harness</c> section of <c>appsettings.Harness.json</c>.
/// They stay apart from <c>Morgana:</c>, which describes the instance under test as <c>Morgana.Web</c> configures it, while these describe how the suite drives it.
/// </summary>
public sealed class HarnessOptions
{
    /// <summary>
    /// Whether the UserGuard stays enabled on the instance under test.
    /// It is off by default, since only the guard's own groups assert it and every guarded turn costs one more model call.
    /// </summary>
    public bool EnableUserGuardrail { get; init; }

    /// <summary>
    /// Whether the PeerGuard stays enabled on the instance under test.
    /// It is off by default, since the groups consulting this installation measure the answering agent and every guarded question costs one more model call.
    /// </summary>
    public bool EnablePeerGuardrail { get; init; }

    /// <summary>Seconds to wait for the host to answer <c>GET /api/morgana/health</c> before giving up.</summary>
    public int StartupTimeoutSeconds { get; init; } = 180;

    /// <summary>Seconds to wait for the webhook delivery of a turn's final message.</summary>
    public int TurnTimeoutSeconds { get; init; } = 180;

    /// <summary>
    /// Milliseconds to wait after a turn's final message before reading the captured host log.
    /// The console logger writes on a background queue, so a tool's log line can trail the webhook delivery by a few milliseconds.
    /// </summary>
    public int LogDrainMilliseconds { get; init; } = 400;

    /// <summary>Default number of runs per scenario when the scenario does not override it.</summary>
    public int DefaultRuns { get; init; } = 5;

    /// <summary>Default number of passing runs required when the scenario does not override it.</summary>
    public int DefaultMinPasses { get; init; } = 4;

    /// <summary>Whether the host's stdout is echoed to the real console, which is noisy and serves diagnosis.</summary>
    public bool EchoHostOutput { get; init; }

    /// <summary>Minimum level for the <c>Morgana</c> log categories on the host under test.</summary>
    public string HostLogLevel { get; init; } = "Information";

    /// <summary>
    /// The revision phase this run measures, such as <c>v0</c> then <c>A2.1</c>: the row key in every journey file.
    /// It is bumped in <c>appsettings.Harness.json</c> for a new phase and set for one run with <c>Harness__Phase</c>; re-running a phase
    /// replaces its row, since a phase is a state of the prose and not a count of measurements.
    /// </summary>
    public string Phase { get; init; } = "v0";

    /// <summary>
    /// Overrides <c>Morgana:HistoryReducer:SummarizationThreshold</c> at boot, when set; unset, every group runs on the inherited value.
    /// The default trigger of 21 messages is far above any scripted scenario, so only <c>SummarizationTests</c> lowers it, in an invocation
    /// of its own, since the change holds for the whole shared host.
    /// </summary>
    public int? SummarizationThreshold { get; init; }

    /// <summary>Overrides <c>Morgana:HistoryReducer:SummarizationTargetCount</c> at boot, when set, beside <see cref="SummarizationThreshold"/>.</summary>
    public int? SummarizationTargetCount { get; init; }

    /// <summary>
    /// Sets <c>Morgana:DustLimiting:BudgetPerConversation</c> and switches dust limiting on for the run, when set; dust limiting is off otherwise.
    /// It holds for the whole shared host, so only <c>DustTests</c> sets it, in an invocation of its own, small enough that a few turns cross 70%, 90% and the end.
    /// </summary>
    public double? DustBudgetPerConversation { get; init; }

    /// <summary>
    /// Switches <c>Morgana:RateLimiting</c> on at boot with this many calls per minute, when set; rate limiting is off otherwise.
    /// It holds for the whole shared host like <see cref="DustBudgetPerConversation"/>, so only <c>RateLimitTests</c> sets it.
    /// </summary>
    public int? RateLimitPerMinute { get; init; }

    /// <summary>
    /// Whether this run stands a second Morgana up and points the instance under test at it as a partner, which <c>FederationTests</c> alone needs.
    /// It replaces the whole domain with the one agent holding a colleague abroad, so it runs on its own:
    /// <c>Harness__FederatedPeer=true dotnet test PromptHarness.csproj --filter "FullyQualifiedName~FederationTests"</c>.
    /// </summary>
    public bool FederatedPeer { get; init; }

    /// <summary>
    /// Whether this run serves the poisoned sources in-process and replaces the domain with the one agent consulting them, which <c>HijackingTests</c> alone needs.
    /// The domain is replaced like <see cref="FederatedPeer"/>'s, so it runs on its own:
    /// <c>Harness__PoisonedDomain=true dotnet test PromptHarness.csproj --filter "FullyQualifiedName~PromptHarness.Tests.HijackingTests"</c>.
    /// </summary>
    public bool PoisonedDomain { get; init; }

    /// <summary>
    /// Whether this run points every tier at a fake model in this process that holds the calls a test chooses, which <c>CancelledTurnTests</c> alone needs.
    /// Every tier, the turn budget and the guards are rewritten, so it never shares an invocation with another knob:
    /// <c>Harness__CancellableModel=true dotnet test PromptHarness.csproj --filter "FullyQualifiedName~PromptHarness.Tests.CancelledTurnTests"</c>.
    /// </summary>
    public bool CancellableModel { get; init; }

    /// <summary>
    /// Names the malformed harness plugin that a <c>PluginValidationTests</c> invocation loads, <c>unlabelled-intent</c> loading the <c>unlabelled-intent-plugins</c> directory.
    /// A plugin once loaded stays in the process and every later boot would refuse, so it runs on its own:
    /// <c>Harness__MalformedDomain=unlabelled-intent dotnet test PromptHarness.csproj --filter "FullyQualifiedName~PromptHarness.Tests.PluginValidationTests"</c>.
    /// </summary>
    public string? MalformedDomain { get; init; }

    /// <summary>
    /// Pins the seal word of the example plugin's InventoryTool to this value for the run instead of a fresh random one.
    /// A scenario cannot carry a value from one turn into a later <c>say:</c>, so one confirming an order recites this word; it reaches the
    /// host as <c>Harness__DeterministicSealWord</c>, the variable the plugin reads.
    /// </summary>
    public string DeterministicSealWord { get; init; } = "534LW03D";

    /// <summary>
    /// Where the journey files and failure reports are written: an absolute path as it is, a relative one against the project directory.
    /// The directory is a local measurement log and never versioned; several people running the suite point it outside the checkout.
    /// </summary>
    public string HarnessDirectory { get; init; } = "Harness";

    /// <summary>Binds the <c>Harness</c> section, falling back to the defaults above when absent.</summary>
    public static HarnessOptions Load(IConfiguration configuration)
        // A file with no "Harness" object binds nothing, so every knob then takes its default.
        => configuration.GetSection("Harness").Get<HarnessOptions>() ?? new HarnessOptions();
}