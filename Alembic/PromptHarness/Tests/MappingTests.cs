using Distiller.Interfaces;
using Distiller.Model;
using PromptHarness.Fixtures;
using PromptHarness.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Tests Alembic's <c>DomainMapper</c> pass alone: does a scripted domain expert's description of
/// their work come back as a well-formed map, before any agent exists.
/// </summary>
/// <remarks>
/// Deliberately outside <see cref="Fixtures.BistroLunaCollection"/>: this pass alone needs nothing
/// past its own two-turn script and joining the collection would buy it nothing but a wait for a
/// full interview it never reads.
/// </remarks>
[Trait("Stage", "Mapping")]
public sealed class MappingTests
{
    private readonly AlembicHostFixture fixture;

    public MappingTests(AlembicHostFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Bistro_Luna_produces_one_intent_with_every_field_set()
    {
        using IServiceScope scope = fixture.NewScope();
        IInterviewService interview = scope.ServiceProvider.GetRequiredService<IInterviewService>();

        DrivenInterview driven = await InterviewDriver.RunMappingOnlyAsync(
            interview, BistroLunaFixture.MappingScript, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(driven.FinalState.Error);
        Assert.True(driven.FinalState.Pass != InterviewStep.DomainMapper,
            $"The mapping pass never settled within the driven exchanges.\n{driven}");

        // Exactly one entry: the client described one process ("check availability, then book"),
        // never asked to name a second and the doctrine is explicit that the map is a choice, not
        // an inventory — a script this narrow producing two or more entries would be the mapper
        // inventing scope nobody asked it to take on.
        Assert.True(driven.FinalState.Map.Count == 1,
            $"Expected exactly one intent, found {driven.FinalState.Map.Count}.\n{driven}");

        IntentDraft intent = driven.FinalState.Map[0];

        // All four fields, per the doctrine's own reasoning: a description is read by the
        // classifier against every other description and a label against every other button —
        // both only correct once the whole set exists, which is exactly what this pass is for.
        Assert.False(string.IsNullOrWhiteSpace(intent.Name), $"Intent has no Name.\n{driven}");
        Assert.False(string.IsNullOrWhiteSpace(intent.Description), $"Intent '{intent.Name}' has no Description.\n{driven}");
        Assert.False(string.IsNullOrWhiteSpace(intent.Label), $"Intent '{intent.Name}' has no Label.\n{driven}");
        Assert.False(string.IsNullOrWhiteSpace(intent.DefaultValue), $"Intent '{intent.Name}' has no DefaultValue.\n{driven}");

        // The fallback intent is never authored by an interview — DeclareIntent refuses the name —
        // so it must not appear on the map itself even though DomainDraft.EnsureFallbackIntent will
        // add it to the Draft later, outside the interview's own bookkeeping.
        Assert.DoesNotContain(driven.FinalState.Map, i =>
            string.Equals(i.Name, DomainDraft.FallbackIntent, StringComparison.OrdinalIgnoreCase));
    }
}
