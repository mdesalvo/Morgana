using Distiller.Interfaces;
using Distiller.Model;
using PromptHarness.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PromptHarness.Fixtures;

/// <summary>
/// Runs the Bistro Luna interview to completion once — the map, both desks and the closing step
/// that settles the colleagues — and shares the result across every test in the collection it is
/// attached to.
/// </summary>
/// <remarks>
/// Every exchange of a driven interview is a live LLM call — three tests each re-running the same
/// scripted interview would triple the cost and the wait for exactly the same Draft. This runs it
/// once, in <see cref="InitializeAsync"/> and every <c>[Fact]</c> in the class asserts on the one
/// result.
/// </remarks>
public sealed class BistroLunaInterviewFixture : IAsyncLifetime
{
    private readonly AlembicHostFixture host;
    private IServiceScope scope = null!;

    /// <summary>The Draft the interview left behind.</summary>
    public DomainDraft Draft { get; private set; } = null!;

    /// <summary>The transcript, kept for a failing assertion's own diagnostics.</summary>
    public DrivenInterview Driven { get; private set; } = null!;

    /// <summary>The scope the interview ran in, so a test can resolve the same DI graph it used.</summary>
    public IServiceProvider Services => scope.ServiceProvider;

    /// <summary>The front desk: the agent the map's table-booking entry produced.</summary>
    public AgentDraft Reservations => Desk(BistroLunaFixture.FrontDesk);

    /// <summary>The events desk: the agent the map's back-room entry produced.</summary>
    public AgentDraft Events => Desk(BistroLunaFixture.EventsDesk);

    /// <summary>
    /// The agent whose intent the client's own words about a desk point at.
    /// </summary>
    /// <remarks>
    /// The interview names its own intents, so a test that wanted "the second agent" would be
    /// asserting about whichever desk the mapper happened to list second. What is stable is the
    /// subject the client described, which is what the intent name and target carry.
    /// </remarks>
    private AgentDraft Desk(IReadOnlyList<string> recognisers) =>
        Draft.Agents
            .Select(agent => (Agent: agent, Hits: recognisers.Count(word =>
                $"{agent.ID} {agent.Target}".Contains(word, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(match => match.Hits)
            .First()
            .Agent;

    public BistroLunaInterviewFixture(AlembicHostFixture host) => this.host = host;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        scope = host.NewScope();
        IInterviewService interview = scope.ServiceProvider.GetRequiredService<IInterviewService>();
        IDraftStateService draftState = scope.ServiceProvider.GetRequiredService<IDraftStateService>();

        Driven = await InterviewDriver.RunFullAsync(interview, BistroLunaFixture.FullScript());
        Draft = draftState.Current ?? throw new InvalidOperationException($"No Draft was produced.\n{Driven}");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        scope.Dispose();
        return ValueTask.CompletedTask;
    }
}
