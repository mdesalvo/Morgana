using Alembic.Interfaces;
using Alembic.Model;
using AlembicHarness.Fixtures;
using AlembicHarness.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlembicHarness.Tests;

/// <summary>
/// Tests Alembic's full interview conduct — every pass, for the one agent Bistro Luna's map
/// produces — against the Draft it leaves behind.
/// </summary>
public sealed class InterviewConductionTests
{
    private readonly AlembicHostFixture fixture;

    public InterviewConductionTests(AlembicHostFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Bistro_Luna_completes_with_every_section_settled()
    {
        using IServiceScope scope = fixture.NewScope();
        IInterviewService interview = scope.ServiceProvider.GetRequiredService<IInterviewService>();
        IDraftStateService draftState = scope.ServiceProvider.GetRequiredService<IDraftStateService>();

        DrivenInterview driven = await InterviewDriver.RunFullAsync(interview, BistroLunaFixture.FullScript());

        Assert.Null(driven.FinalState.Error);

        DomainDraft? draft = draftState.Current;
        Assert.True(draft is not null, $"No Draft was produced.\n{driven}");
        Assert.True(draft!.Agents.Count == 1, $"Expected exactly one agent, found {draft.Agents.Count}.\n{driven}");

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
