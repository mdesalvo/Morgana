using Xunit;

namespace PromptHarness.Fixtures;

/// <summary>
/// Groups every test that reads the one completed Bistro Luna interview, so xunit builds
/// <see cref="BistroLunaInterviewFixture"/> once for the whole collection rather than once per class.
/// </summary>
/// <remarks>
/// <see cref="Tests.DoctrineTests"/>, <see cref="Tests.FinalizationTests"/> and
/// <see cref="Tests.InterviewConductionTests"/> all assert on the same finished Draft and none of
/// them mutates it — before this collection existed, each held its own <c>IClassFixture</c> (or, for
/// interview conduct, drove its own separate run) and so each paid for its own live, ~15-turn
/// interview, three real conversations through the LLM for the one Draft the suite actually needs.
/// One collection, one fixture instance, three classes reading it — the mapping pass alone stays
/// outside it, since <see cref="Tests.MappingTests"/> needs nothing past its own two-turn run and
/// gains nothing from the shared instance.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class BistroLunaCollection : ICollectionFixture<BistroLunaInterviewFixture>
{
    /// <summary>The name every member class's <c>[Collection]</c> attribute refers back to.</summary>
    public const string Name = "Bistro Luna interview";
}
