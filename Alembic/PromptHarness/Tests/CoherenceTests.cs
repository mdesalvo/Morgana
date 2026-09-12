using Distiller2.Interfaces;
using Distiller2.Model;
using Microsoft.Extensions.DependencyInjection;
using PromptHarness.Fixtures;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The coherence pass over the finished Bistro Luna domain, asked the one question the interview
/// cannot settle on its own: whether an agent's declared colleagues and its own prose agree.
/// </summary>
/// <remarks>
/// Every other live class in this suite reads what the interview wrote. This one reads the domain
/// the way the client's own reviewer does, which is the only reader that sees an edge and the prose
/// beside it at the same time — the interview writes each agent alone by construction.
/// <para>
/// Two runs, and the second is why the first means anything. A pass that reported nothing whatever
/// would pass the clean domain exactly as a working one does, so the same domain is handed back with
/// the hand-off sentence put in again under the edge that is still declared — the defect this class
/// of finding is named for — and the pass has to see it. One aspect is asked for in both runs and
/// never the whole list: an unselected class is not sent at all, so what comes back is an answer
/// about the question that was put rather than a table to sift.
/// </para>
/// </remarks>
[Collection(BistroLunaCollection.Name)]
[Trait("Stage", "Coherence")]
public sealed class CoherenceTests
{
    /// <summary>The class of defect this reads for, as the pass's own prompt declares it.</summary>
    private const string ColleagueOutOfStep = "colleague-out-of-step";

    /// <summary>
    /// What the front desk's prose said before the closing step struck it: the subject sent to
    /// another desk, while the desk that answers it is a declared colleague.
    /// </summary>
    private const string HandOff =
        " Private events and the back room are not handled here: tell the customer to ring our events "
        + "office on the other number instead.";

    private readonly BistroLunaInterviewFixture interviewed;

    public CoherenceTests(BistroLunaInterviewFixture interviewed) => this.interviewed = interviewed;

    [Fact]
    public async Task The_finished_domain_reads_as_in_step_with_its_own_colleagues()
    {
        ICoherenceService coherence = interviewed.Services.GetRequiredService<ICoherenceService>();

        CoherenceReport report = await coherence.ReviewAsync(
            interviewed.Draft, [ColleagueOutOfStep], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(report.Error);
        Assert.True(report.Findings.Count == 0,
            $"The pass found the domain out of step with its own colleagues:\n"
            + $"{string.Join('\n', report.Findings.Select(f => $"- [{f.Kind}] {f.Where}: {f.What}"))}\n{interviewed.Driven}");
    }

    [Fact]
    public async Task The_same_domain_with_the_hand_off_put_back_is_reported()
    {
        DomainDraft sabotaged = await CopyAsync();

        AgentDraft reservations = sabotaged.Agents.Single(a =>
            string.Equals(a.ID, interviewed.Reservations.ID, StringComparison.OrdinalIgnoreCase));

        reservations.Instructions += HandOff;

        ICoherenceService coherence = interviewed.Services.GetRequiredService<ICoherenceService>();

        CoherenceReport report = await coherence.ReviewAsync(
            sabotaged, [ColleagueOutOfStep], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(report.Error);
        Assert.Contains(report.Findings, f =>
            string.Equals(f.Kind, ColleagueOutOfStep, StringComparison.OrdinalIgnoreCase)
            && f.Where.Contains(reservations.ID!, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The finished domain, detached from the one every other test in the collection reads.
    /// </summary>
    /// <remarks>
    /// Taken through the save file rather than field by field: what has to be identical is the whole
    /// domain the pass is handed, colleagues and C# facts included, and a copy written by hand here
    /// would be a second definition of what a domain is, drifting from the first.
    /// </remarks>
    private async Task<DomainDraft> CopyAsync()
    {
        IDraftSerializationService serialization =
            interviewed.Services.GetRequiredService<IDraftSerializationService>();

        using MemoryStream saved = new MemoryStream(serialization.Serialize(interviewed.Draft));

        return await serialization.DeserializeAsync(saved, TestContext.Current.CancellationToken)
               ?? throw new InvalidOperationException("The finished domain did not survive its own save file.");
    }
}
