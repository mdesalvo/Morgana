using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group covering a colleague that is not an agent of this installation at all, but of another
/// Morgana running beside it.
/// </summary>
/// <remarks>
/// <para><strong>Requires a second installation at boot</strong> — the instance under test carries a
/// domain of one desk on this run and would find every other group's agents missing. Run it on its
/// own:</para>
/// <code>Harness__FederatedPeer=true dotnet test PromptHarness.csproj --filter "FullyQualifiedName~FederationTests"</code>
///
/// <para>What only this group can show: that the two halves of the protocol meet. Everything on the
/// way out is measured against a stub peer by <c>PeerFederationTests</c> and everything on the way in
/// against a stub caller by <c>ServedConsultationTests</c> — but both stubs were written here, so
/// neither can catch the two ends disagreeing about what a card, an issuer or an audience is. Here
/// the card is written by a Morgana, read by a Morgana and the token one minted is proven by the
/// other.</para>
///
/// <para>Its one scenario asserts the mechanism and the datum, nothing around it. The desk doing the
/// asking has no books and no tools, so a turn that states a stock level at all has been across the
/// wire — which is why no assertion here has to reach into the other installation to see what
/// happened there.</para>
/// </remarks>
public sealed class FederationTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public FederationTests(MorganaHostFixture fixture) => this.fixture = fixture;

    /// <summary>
    /// Replays the federation scenario and asserts it cleared its own pass threshold, printing the
    /// full per-run transcript on the assertion message when it did not.
    /// </summary>
    [Fact]
    public async Task A_desk_reaches_its_colleague_at_the_other_installation()
    {
        // Without the second installation this run's instance is the ordinary one, whose domain has
        // no desk declaring a colleague abroad: there is nothing here to measure rather than
        // something failing.
        Assert.SkipWhen(fixture.Peer is null,
            "This group needs the second installation: Harness__FederatedPeer=true.");

        ScenarioOutcome outcome = await fixture.Runner.RunAsync("federation-consults-a-desk-at-another-installation");

        Assert.True(outcome.Passed, outcome.Report());
    }
}
