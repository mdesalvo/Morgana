using Alembic.Interfaces;
using Alembic.Model;
using AlembicHarness.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlembicHarness.Fixtures;

/// <summary>
/// Runs the Bistro Luna interview to completion once and shares the result across every test in
/// the class it is attached to, via <see cref="IClassFixture{TFixture}"/>.
/// </summary>
/// <remarks>
/// Every exchange of a driven interview is a live LLM call — three tests each re-running the same
/// scripted interview would triple the cost and the wait for exactly the same Draft. This runs it
/// once, in <see cref="InitializeAsync"/>, and every <c>[Fact]</c> in the class asserts on the one
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
