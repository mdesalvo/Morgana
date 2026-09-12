using Distiller.Interfaces;
using Distiller.Model;
using Microsoft.Extensions.DependencyInjection;
using Morgana.AI;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The two jobs a step can be doing, as they reach the model: composing a section that does not
/// exist, or correcting one that does.
/// </summary>
/// <remarks>
/// No model is called here and none would help — what is asserted is the shape of the prompt, which
/// is decidable by reading it. It is worth asserting because both alternatives to this shape failed
/// in ways nothing downstream catches. Written as clauses inside each pass's prose, the two jobs sat
/// in every pass and were read on every run, which is how a fully written agent was twice opened as
/// a blank one. Written as two files they would have been nine tenths identical, tool declarations
/// included and the last time this prompt held two copies of anything one had already drifted from
/// the other.
/// <para>
/// So the mode is a third row, merged section by section beneath the shared prose and these tests
/// hold it to exactly that: every pass gets one of the two blocks, gets the right one and gets
/// nothing else different.
/// </para>
/// </remarks>
[Trait("Stage", "Prompting")]
public sealed class ModePromptTests
{
    private readonly AlembicHostFixture fixture;

    public ModePromptTests(AlembicHostFixture fixture) => this.fixture = fixture;

    /// <summary>Every pass of the interview: all six are entered both ways.</summary>
    public static TheoryData<InterviewStep> Passes => [.. Enum.GetValues<InterviewStep>()];

    /// <summary>
    /// How a pass tells one job from the other — the words each mode's block opens on and the same
    /// words <c>InterviewService</c> puts in the message that opens the step.
    /// </summary>
    private const string Composing = "YOU ARE COMPOSING.";

    /// <inheritdoc cref="Composing" />
    private const string Correcting = "YOU ARE CORRECTING.";

    [Theory]
    [MemberData(nameof(Passes))]
    public async Task Each_pass_is_told_which_of_the_two_jobs_it_is_doing(InterviewStep pass)
    {
        (string composing, string correcting) = await Both(pass);

        Assert.Contains(Composing, composing, StringComparison.Ordinal);
        Assert.DoesNotContain(Correcting, composing, StringComparison.Ordinal);

        Assert.Contains(Correcting, correcting, StringComparison.Ordinal);
        Assert.DoesNotContain(Composing, correcting, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Passes))]
    public async Task The_two_jobs_differ_by_the_mode_row_and_by_nothing_else(InterviewStep pass)
    {
        (string composing, string correcting) = await Both(pass);

        // Paragraph by paragraph, because that is the unit the prompt is written in and a diff of
        // whole strings would only say they differ. What each composed prompt holds that the other
        // does not must be precisely its own mode row — no more, so a rule cannot come to exist in
        // one job and not the other; and no less, so the row cannot silently stop being injected.
        Assert.Equal<IEnumerable<string>>(
            Paragraphs(Row("Composing")),
            Paragraphs(composing).Except(Paragraphs(correcting)));

        Assert.Equal<IEnumerable<string>>(
            Paragraphs(Row("Correcting")),
            Paragraphs(correcting).Except(Paragraphs(composing)));
    }

    [Fact]
    public void Correcting_tells_a_pass_how_to_read_its_own_composing_instructions()
    {
        // The sentence that lets every pass keep its own procedure written plainly, in one voice,
        // with no conditional in it: a pass reopened over a written section is told that its own
        // running order is describing the other job. Without it the conditionals come back, one per
        // pass and that is the arrangement that failed.
        Assert.Contains("describing the other job", Row("Correcting"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Neither_mode_row_carries_anything_but_conducting()
    {
        // A row, not a fourth pass. It says how this step is to be gone about and never what any
        // step settles — a mode that started naming sections would be a rule about an agent living
        // somewhere no pass declares a tool for it.
        foreach (string id in new[] { "Composing", "Correcting" })
        {
            Records.Prompt mode = Prompts.Resolve(id);

            Assert.True(string.IsNullOrWhiteSpace(mode.Target), $"{id} carries a Target.");
            Assert.True(string.IsNullOrWhiteSpace(mode.Personality), $"{id} carries a Personality.");
            Assert.True(string.IsNullOrWhiteSpace(mode.Formatting), $"{id} carries a Formatting.");
            Assert.False(string.IsNullOrWhiteSpace(mode.Instructions), $"{id} carries no Instructions.");
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// Alembic's own prompts. A Singleton, so the scope is only how this harness reaches the graph
    /// and outlives nothing that matters.
    /// </summary>
    private IAlembicPromptService Prompts
    {
        get
        {
            using IServiceScope scope = fixture.NewScope();
            return scope.ServiceProvider.GetRequiredService<IAlembicPromptService>();
        }
    }

    /// <summary>One pass, composed both ways. The prompt id of a pass is its own enum name.</summary>
    // Awaited rather than blocked on. ComposeAsync resolves Morgana's own framework prompt behind a
    // Lazy<Task> and blocking a test thread on it deadlocks against xunit's synchronization context
    // — which is invisible when this class runs on its own and hangs the whole assembly when it does
    // not, since what changes is only how the run is scheduled.
    private async Task<(string Composing, string Correcting)> Both(InterviewStep pass)
    {
        IAlembicPromptService prompts = Prompts;

        return (await prompts.ComposeAsync(pass.ToString(), correcting: false),
                await prompts.ComposeAsync(pass.ToString(), correcting: true));
    }

    /// <summary>One mode's row, as authored.</summary>
    private string Row(string id) => Prompts.Resolve(id).Instructions ?? string.Empty;

    private static string[] Paragraphs(string prose) =>
        [.. prose.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
