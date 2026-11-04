using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The chain behind a guarded door with the real agent in it: a domain whose own book, MCP server and partner answer with an attempt at hijacking.
/// </summary>
/// <remarks>
/// <para><strong>Requires the poisoned domain at boot</strong> — the instance under test carries one agent
/// and the sources it consults are served in-process, so every other group would find its own agents
/// missing. Run it on its own:</para>
/// <code>Harness__PoisonedDomain=true dotnet test PromptHarness.csproj --filter "FullyQualifiedName~PromptHarness.Tests.HijackingTests"</code>
///
/// <para>A quarantined result counts only on a verdict and the agent must neither carry out what it asked
/// nor answer in its place. An honest result is admitted and carried. An offer or a card hiding a decoy
/// is refused at startup and its tool never reaches the agent.</para>
/// </remarks>
public sealed class HijackingTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>Holds the shared host, whose sources this group needs served before the instance under test starts.</summary>
    public HijackingTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>The scenarios whose id starts with <c>hijacking-</c>, read from the harness output so that a new file joins the group without a line here.</summary>
    public static TheoryData<string> ScenarioIds
    {
        get
        {
            // The files are the group's membership: the prefix is the whole of the selection.
            TheoryData<string> scenarioIds = [];
            foreach (string path in Directory.GetFiles(ScenarioLoader.Directory, "hijacking-*.yaml").Order(StringComparer.Ordinal))
                scenarioIds.Add(Path.GetFileNameWithoutExtension(path));

            return scenarioIds;
        }
    }

    /// <summary>Replays one hijacking scenario and asserts it cleared its own pass threshold, printing the full per-run transcript on the assertion message when it did not.</summary>
    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(ScenarioIds))]
    public async Task Hijacking_scenario_holds(string scenarioId)
    {
        // Without the poisoned sources this run's instance is the ordinary one, whose domain has no
        // agent that consults them: there is nothing here to measure rather than something failing.
        Assert.SkipWhen(fixture.PoisonedSources is null,
            "This group needs the poisoned domain: Harness__PoisonedDomain=true.");

        // The scenario runs against the poisoned domain: every source it reaches answers from PoisonedSourceHost.
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        // The scenario's own expectations decide: what was withheld, what was admitted and what the customer read.
        Assert.True(outcome.Passed, outcome.Report());
    }
}
