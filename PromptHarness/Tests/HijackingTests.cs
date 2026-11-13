using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The chain behind a guarded door with the real agent in it: a domain whose own book, MCP server and partner answer with an attempt at hijacking.
/// It needs Harness__PoisonedDomain=true in an invocation of its own, since the instance then carries one agent and every other group would miss its own.
/// A quarantined result counts only on a verdict, an honest result is admitted and an offer or a card hiding a decoy never reaches the agent.
/// </summary>
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