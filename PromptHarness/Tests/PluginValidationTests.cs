using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting that a domain built to be wrong stops the instance at boot rather than
/// reaching a user as a welcome that cannot be offered.
/// </summary>
/// <remarks>
/// <para><strong>Requires the malformed domain named at boot</strong>, process-wide like the dust budget.
/// Run this class on its own, one invocation per malformed domain, because a plugin once loaded stays in
/// the process and every later boot in it would refuse:</para>
/// <code>Harness__MalformedDomain=unlabelled-intent dotnet test PromptHarness.csproj --filter "FullyQualifiedName~PromptHarness.Tests.PluginValidationTests"</code>
/// <para>Deterministic and free, no model is reached: each case boots the <b>real</b> entry point with
/// one harness plugin built to be wrong beside the shipped domain and expects the refusal. The live host
/// of the fixture never loads that plugin.</para>
/// </remarks>
public sealed class PluginValidationTests
{
    /// <summary>The live host, read for the knob this invocation was started with.</summary>
    private readonly MorganaHostFixture fixture;

    public PluginValidationTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Fact]
    public void Boot_is_refused_when_an_intent_has_no_label()
    {
        // The plugin is loaded by the doomed boot alone: a run that did not name it would find the
        // refusal missing for a reason that has nothing to do with the startup check.
        Assert.SkipWhen(fixture.Options.MalformedDomain != "unlabelled-intent",
            "The malformed domain is not named: run with Harness__MalformedDomain=unlabelled-intent, in this class's own invocation.");

        // Directory 0 stays the fixture's shipped domain: the malformed one is added beside it as the second entry.
        Exception refusal = DoomedHost.AssertRefusesToBoot(
            ("Morgana__Plugins__Directories__1", "unlabelled-intent-plugins"));

        // The refusal names the intent and the field a domain author has to add.
        Assert.Contains("unlabelled", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Label", refusal.Message, StringComparison.Ordinal);
    }
}
