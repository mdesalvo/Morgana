using PromptHarness.Infrastructure.Engine;
using PromptHarness.Infrastructure.Wiring;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The guard on tool results, against a domain whose every source answers with prompt injection:
/// the plugin's own tools, an MCP server and a partner's colleague, each carrying five families of
/// hijacking (personality, authority, behaviour, data, the user).
/// </summary>
/// <remarks>
/// <para><strong>Requires <c>Harness:EnableToolGuardrail=true</c></strong>, which swaps the domain for
/// <c>PoisonedPlugin</c> and stands up <c>PoisonedSourceHost</c> on a fixed port. Run this class on
/// its own:</para>
/// <code>Harness__EnableToolGuardrail=true dotnet test PromptHarness.csproj --filter "FullyQualifiedName~PromptHarness.Tests.ToolGuardTests"</code>
///
/// <para>Run without the flag, every scenario fails loudly: the domain's agent does not exist and no
/// result is ever screened, so nothing can be reported quarantined by accident.</para>
///
/// <para>Two scenarios hold the other half of the contract: a truthful result from each source the
/// inspector reads must pass, quick replies and card included, or a guard refusing everything would
/// pass every other scenario here.</para>
///
/// <para>The hijacking scenarios hold security at 5/5, so they judge only what must never happen: the
/// hijacking carried out or the answer given anyway. How a refusal is worded is lexical and admits
/// nuance, so it is held once, at 5/4, by <c>toolguard-refusal-tone</c>.</para>
/// </remarks>
public sealed class ToolGuardTests
{
    /// <summary>The live host, shared with every other test class in the assembly.</summary>
    private readonly MorganaHostFixture fixture;

    public ToolGuardTests(MorganaHostFixture fixture) => this.fixture = fixture;

    [Theory]
    [InlineData("toolguard-native-personality")]
    [InlineData("toolguard-native-authority")]
    [InlineData("toolguard-native-behaviour")]
    [InlineData("toolguard-native-data")]
    [InlineData("toolguard-native-user")]
    [InlineData("toolguard-mcp-personality")]
    [InlineData("toolguard-mcp-authority")]
    [InlineData("toolguard-mcp-behaviour")]
    [InlineData("toolguard-mcp-data")]
    [InlineData("toolguard-mcp-user")]
    [InlineData("toolguard-mcp-clean")]
    [InlineData("toolguard-partner-personality")]
    [InlineData("toolguard-partner-authority")]
    [InlineData("toolguard-partner-behaviour")]
    [InlineData("toolguard-partner-data")]
    [InlineData("toolguard-partner-user")]
    [InlineData("toolguard-partner-clean")]
    [InlineData("toolguard-refusal-tone")]
    public async Task ToolGuard_scenario_holds(string scenarioId)
    {
        ScenarioOutcome outcome = await fixture.Runner.RunAsync(scenarioId);

        Assert.True(outcome.Passed, outcome.Report());
    }
}
