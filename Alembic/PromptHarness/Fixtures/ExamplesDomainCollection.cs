using Xunit;

namespace PromptHarness.Fixtures;

/// <summary>
/// Groups every test that reads the one corrected <c>Examples</c> domain, so xunit builds
/// <see cref="ExamplesDomainFixture"/> once for the whole collection rather than once per class.
/// </summary>
/// <remarks>
/// Separate from <see cref="BistroLunaCollection"/> because it starts from the other end. That one
/// interviews a domain into existence and asks what Alembic wrote; this one takes a configuration
/// somebody already runs and asks what Alembic does to it — which is the only way to have an agent
/// marked <see cref="Distiller.Model.Provenance.Imported"/> to correct in the first place.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class ExamplesDomainCollection : ICollectionFixture<ExamplesDomainFixture>
{
    /// <summary>The name every member class's <c>[Collection]</c> attribute refers back to.</summary>
    public const string Name = "Examples domain correction";
}
