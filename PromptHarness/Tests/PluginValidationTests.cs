using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting that a domain built to be wrong stops the instance at boot rather than reaching a user as a welcome that cannot be offered.
/// It needs Harness__MalformedDomain and an invocation of its own per malformed domain, since a plugin once loaded stays in the process.
/// Deterministic and free: each case boots the real entry point with the malformed plugin beside the shipped domain, which the live host never loads.
/// </summary>
public sealed class PluginValidationTests
{
    /// <summary>The live host, read for the knob this invocation was started with.</summary>
    private readonly MorganaHostFixture fixture;

    /// <summary>Receives the assembly's one live host, read only for its knob.</summary>
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