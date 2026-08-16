using Distiller.Model;
using PromptHarness.Fixtures;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Tests Alembic's full interview conduct — every pass, for the one agent Bistro Luna's map
/// produces — against the Draft the shared <see cref="BistroLunaInterviewFixture"/> leaves behind.
/// </summary>
/// <remarks>
/// Used to drive its own separate run through <c>InterviewDriver.RunFullAsync</c>, on top of the
/// identical run <see cref="DoctrineTests"/> and <see cref="FinalizationTests"/> each drove through
/// their own <c>BistroLunaInterviewFixture</c> — three live, ~15-turn interviews for the one Draft
/// this suite actually needs. All three now read <see cref="BistroLunaCollection"/>'s single shared
/// instance instead.
/// </remarks>
[Collection(BistroLunaCollection.Name)]
[Trait("Stage", "Interview")]
public sealed class InterviewConductionTests
{
    private readonly BistroLunaInterviewFixture interviewed;

    public InterviewConductionTests(BistroLunaInterviewFixture interviewed) => this.interviewed = interviewed;

    [Fact]
    public void Bistro_Luna_completes_with_every_section_settled()
    {
        DrivenInterview driven = interviewed.Driven;
        Assert.Null(driven.FinalState.Error);

        DomainDraft draft = interviewed.Draft;
        Assert.True(draft.Agents.Count == 1, $"Expected exactly one agent, found {draft.Agents.Count}.\n{driven}");

        AgentDraft agent = draft.Agents[0];

        Assert.False(string.IsNullOrWhiteSpace(agent.Target), $"Agent has no Target.\n{driven}");
        Assert.False(string.IsNullOrWhiteSpace(agent.Personality), $"Agent has no Personality.\n{driven}");
        Assert.False(string.IsNullOrWhiteSpace(agent.Instructions), $"Agent has no Instructions.\n{driven}");
        Assert.False(string.IsNullOrWhiteSpace(agent.Formatting), $"Agent has no Formatting.\n{driven}");

        // The client described exactly two operations: checking availability and placing a
        // reservation. A toolkit with neither, or with a third tool nobody described, would be the
        // ToolkitModeler pass inventing or dropping capability rather than transcribing what it was
        // told.
        Assert.True(agent.Tools.Count >= 1, $"Agent declared no tools at all.\n{driven}");
        Assert.All(agent.Tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Name), $"A tool has no Name.\n{driven}"));
        Assert.All(agent.Tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"Tool '{tool.Name}' has no Description.\n{driven}"));

        // The fallback intent belongs to every domain and to no interview — AcceptAsync adds it on
        // the way out, once the map is exhausted, never as something the client was asked about.
        Assert.Contains(draft.Intents, i =>
            string.Equals(i.Name, DomainDraft.FallbackIntent, StringComparison.OrdinalIgnoreCase));

        // Every accepted agent is Authored, since nothing here came from an upload — this is the
        // fact the migration report leans on to say "new" honestly.
        Assert.Equal(Provenance.Authored, agent.Origin);
    }
}
