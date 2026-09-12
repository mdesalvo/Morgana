using Distiller.Interfaces;
using Distiller.Model;
using Microsoft.Extensions.DependencyInjection;
using Morgana.AI;
using Morgana.AI.Interfaces;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// What Alembic knows about the framework it is authoring for, as it reaches the model.
/// </summary>
/// <remarks>
/// No model is called here and none would help: every proposition below is decidable by reading the
/// composed prompt. It is worth asserting because the primer is prose about another file — the
/// framework's policies live in <c>morgana.json</c> and their author-facing lines in
/// <c>alembic.json</c>, so the one drift nothing else notices is a policy gaining, losing or
/// renaming itself on one side alone. That leaves an author forbidden a subject nobody named to
/// them, which is the state the primer was written to end.
/// </remarks>
[Trait("Stage", "Prompting")]
public sealed class PrimerPromptTests
{
    private readonly AlembicHostFixture fixture;

    public PrimerPromptTests(AlembicHostFixture fixture) => this.fixture = fixture;

    /// <summary>Every pass of the interview: all of them are written for the same world.</summary>
    public static TheoryData<InterviewStep> Passes => [.. Enum.GetValues<InterviewStep>()];

    [Fact]
    public async Task Every_framework_policy_reaches_the_author_with_what_it_settles()
    {
        Records.Prompt morgana = await Morgana.ResolveAsync("Morgana");
        string composed = await Compose(InterviewStep.AgentToolkit);

        foreach (Records.GlobalPolicy policy in Policies(morgana))
        {
            // The name alone was the whole of this block once and it forbade subjects the reader
            // could not name. Both halves are asserted together because either without the other is
            // the defect: a line with no name belongs to nothing, a name with no line binds nobody.
            Assert.Contains(policy.Name, composed, StringComparison.Ordinal);
            Assert.Contains(Gloss(policy.Name), composed, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task The_primer_covers_the_framework_and_nothing_it_no_longer_has()
    {
        Records.Prompt morgana = await Morgana.ResolveAsync("Morgana");

        Assert.Equal(
            [.. Policies(morgana).Select(policy => policy.Name).Order()],
            [.. Glosses().Select(policy => policy.Name).Order()]);
    }

    [Theory]
    [MemberData(nameof(Passes))]
    public async Task Every_pass_is_told_the_world_its_agents_will_run_in(InterviewStep pass)
    {
        string composed = await Compose(pass);

        // The four facts an author cannot derive from the domain in front of them: how a message
        // reaches one agent rather than another, in what order a turn is built, what the runtime
        // adds to the prose written here and what every agent can already do with no tool declared
        // for it. A pass missing them writes against a framework of its own invention.
        Assert.Contains("HOW A MESSAGE FINDS ITS AGENT", composed, StringComparison.Ordinal);
        Assert.Contains("HOW A TURN IS FORMED", composed, StringComparison.Ordinal);
        Assert.Contains("WHAT IS ADDED TO WHAT YOU WRITE", composed, StringComparison.Ordinal);
        Assert.Contains("WHAT EVERY AGENT ALREADY HAS", composed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_primer_stands_on_its_own_for_the_passes_outside_the_fence()
    {
        string primer = await Prompts.ComposeFrameworkPrimerAsync();

        // The coherence pass and the pass applying its findings take the framework without Morgana's
        // voice: one answers JSON and the other writes for an agent; neither speaks as her. So
        // the block has to close itself — run unterminated into a pass's own prose and the world
        // facts read as the opening of its instructions.
        Assert.StartsWith("-------- HOW SHE RUNS WHAT YOU WRITE", primer, StringComparison.Ordinal);
        Assert.EndsWith("--------", primer.TrimEnd(), StringComparison.Ordinal);

        // One primer, not two: what those passes are told and what the interview is told must be the
        // same account of the framework; otherwise a finding is judged against a world the interview
        // never wrote in.
        Assert.Contains(primer, await Compose(InterviewStep.AgentInstructions), StringComparison.Ordinal);
    }

    [Fact]
    public void The_primer_states_the_world_and_conducts_nothing()
    {
        // A fifth pass is what this must never become: the primer describes the framework and every
        // sentence about how to conduct a step, what it settles or how it answers belongs to the
        // shared prose and to the pass itself, where the tools that carry it out are declared.
        Records.Prompt primer = Prompts.Resolve("MorganaPrimer");

        Assert.False(string.IsNullOrWhiteSpace(primer.Target), "The primer carries no Target.");
        Assert.True(string.IsNullOrWhiteSpace(primer.Personality), "The primer carries a Personality.");
        Assert.True(string.IsNullOrWhiteSpace(primer.Instructions), "The primer carries Instructions.");
        Assert.True(string.IsNullOrWhiteSpace(primer.Formatting), "The primer carries a Formatting.");
    }

    // ---------------------------------------------------------------- helpers

    private IAlembicPromptService Prompts
    {
        get
        {
            using IServiceScope scope = fixture.NewScope();
            return scope.ServiceProvider.GetRequiredService<IAlembicPromptService>();
        }
    }

    /// <summary>Morgana's own framework prompt, the authority on which policies exist.</summary>
    private IPromptResolverService Morgana
    {
        get
        {
            using IServiceScope scope = fixture.NewScope();
            return scope.ServiceProvider.GetRequiredService<IPromptResolverService>();
        }
    }

    private Task<string> Compose(InterviewStep pass) => Prompts.ComposeAsync(pass.ToString());

    private static List<Records.GlobalPolicy> Policies(Records.Prompt prompt) =>
        prompt.GetAdditionalPropertyOrDefault<List<Records.GlobalPolicy>>(Constants.PromptProperties.GlobalPolicies, []);

    private List<Records.GlobalPolicy> Glosses() => Policies(Prompts.Resolve("MorganaPrimer"));

    private string Gloss(string policyName) => Glosses().Single(policy => policy.Name == policyName).Description;
}
