using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group covering a colleague that is an agent of another Morgana running beside this one, booted with Harness__FederatedPeer=true in an invocation of its own.
/// The stub peer of PeerFederationTests and the stub caller of ServedConsultationTests were both written here, so only two real Morganas show the halves of the protocol agreeing.
/// The asking agent has no tools, so a turn that states a stock level at all has been across the wire.
/// </summary>
public sealed class FederationTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>Receives the assembly's one live host, with its second installation when the knob raised one.</summary>
    public FederationTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>Replays the federation scenario and asserts it cleared its own pass threshold.</summary>
    [Fact]
    public async Task An_agent_reaches_its_colleague_at_the_other_installation()
    {
        // Without the second installation this run's instance is the ordinary one, whose domain has
        // no agent declaring a colleague abroad: there is nothing here to measure rather than
        // something failing.
        Assert.SkipWhen(fixture.Peer is null,
            "This group needs the second installation: Harness__FederatedPeer=true.");

        // The dispatch agent asks its colleague at the annex for a datum only that installation holds.
        ScenarioOutcome outcome = await fixture.Runner.RunAsync("federation-consults-an-agent-at-another-installation");

        // The full per-run transcript is the assertion message when the threshold was missed.
        Assert.True(outcome.Passed, outcome.Report());
    }
}