using PromptHarness.Infrastructure;
using Xunit;

// One Alembic service graph for the whole assembly: nothing about it is per-test state (the
// Scoped pieces are re-created per test via AlembicHostFixture.NewScope), and the LLM client and
// prompt services are exactly as expensive to build here as they are for Alembic itself at startup.
[assembly: AssemblyFixture(typeof(AlembicHostFixture))]

// Strictly serial, for the same reason PromptHarness is: every turn of a scripted interview is a
// live LLM call, and running several interviews at once would multiply the burst rate against the
// provider without meaningfully shortening the suite — Alembic itself runs one interview at a time
// per circuit, and this harness has no reason to be more concurrent than the thing it is testing.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
