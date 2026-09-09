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
    public async Task Bistro_Luna_produces_one_intent_per_desk_with_every_field_set()
    {
        using IServiceScope scope = fixture.NewScope();
        IInterviewService interview = scope.ServiceProvider.GetRequiredService<IInterviewService>();

        DrivenInterview driven = await InterviewDriver.RunMappingOnlyAsync(
            interview, BistroLunaFixture.MappingScript, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(driven.FinalState.Error);
        Assert.True(driven.FinalState.Pass != InterviewStep.DomainMapper,
            $"The mapping pass never settled within the driven exchanges.\n{driven}");

        // Exactly two entries: the client named two processes and said so, and the doctrine is
        // explicit that the map is a choice rather than an inventory — a third entry would be the
        // mapper taking on scope nobody asked it to take on, and a single one would be it folding
        // two desks the client keeps apart into one intent the classifier cannot split.
        Assert.True(driven.FinalState.Map.Count == 2,
            $"Expected exactly two intents, found {driven.FinalState.Map.Count}.\n{driven}");

        foreach (IntentDraft intent in driven.FinalState.Map)
        {
            // All four fields, per the doctrine's own reasoning: a description is read by the
            // classifier against every other description and a label against every other button —
            // both only correct once the whole set exists, which is exactly what this pass is for.
            Assert.False(string.IsNullOrWhiteSpace(intent.Name), $"An intent has no Name.\n{driven}");
            Assert.False(string.IsNullOrWhiteSpace(intent.Description), $"Intent '{intent.Name}' has no Description.\n{driven}");
            Assert.False(string.IsNullOrWhiteSpace(intent.Label), $"Intent '{intent.Name}' has no Label.\n{driven}");
            Assert.False(string.IsNullOrWhiteSpace(intent.DefaultValue), $"Intent '{intent.Name}' has no DefaultValue.\n{driven}");
        }

        // Both desks the client described are on the map and they are two different entries. Every
        // later test in this suite is about one desk or about what passes between the two, so a map
        // that answered for only one of them would leave those tests asserting against a domain the
        // client never described.
        Assert.True(Recognised(driven, BistroLunaFixture.FrontDesk) != Recognised(driven, BistroLunaFixture.EventsDesk),
            $"The two desks the client described did not come back as two distinct intents.\n{driven}");

        // The fallback intent is never authored by an interview — DeclareIntent refuses the name —
        // and nothing adds it afterwards either: it is the complement of the domain, which Morgana's
        // classifier owns and describes in its own prompt.
        Assert.DoesNotContain(driven.FinalState.Map, i =>
            string.Equals(i.Name, DomainDraft.FallbackIntent, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The entry the client's own words about one desk point at, by its place on the map.</summary>
    private static int Recognised(DrivenInterview driven, IReadOnlyList<string> recognisers) =>
        driven.FinalState.Map
            .Select((intent, at) => (At: at, Hits: recognisers.Count(word =>
                $"{intent.Name} {intent.Description} {intent.Label}".Contains(word, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(match => match.Hits)
            .First()
            .At;
}
