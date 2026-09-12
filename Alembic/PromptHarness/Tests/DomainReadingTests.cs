using Distiller.Model;
using Microsoft.Extensions.DependencyInjection;
using PromptHarness.Fixtures;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// What Alembic works out about a business from a configuration somebody else finished.
/// </summary>
/// <remarks>
/// The only place Alembic writes down facts about a client's work without anybody having told it
/// any of them. A save file carries the interview's own memory of the trade; a bare
/// <c>agents.json</c> carries finished prose and nothing of the conversation behind it, so an
/// interview reopened over one would know what was decided and nothing about the shop it was
/// decided for. What is asserted here is both halves of that bargain: that the reading is worth
/// having, and that it never passes itself off as something the client said.
/// </remarks>
[Collection(ExamplesDomainCollection.Name)]
[Trait("Stage", "Import")]
public sealed class DomainReadingTests
{
    private readonly ExamplesDomainFixture imported;

    public DomainReadingTests(ExamplesDomainFixture imported) => this.imported = imported;

    private Judge Judge => imported.Services.GetRequiredService<Judge>();

    /// <summary>Everything the reading wrote, desks and business alike, as one body of text.</summary>
    private string Read => string.Join(
        "\n",
        imported.Draft.Learned
            .Concat(imported.Draft.Agents.SelectMany(agent => agent.Known))
            .Select(fact => $"- {fact.Subject}: {fact.Fact}"));

    [Fact]
    public void Reading_an_uploaded_domain_yields_something_to_work_from()
    {
        Assert.Null(imported.Reading.Error);
        Assert.True(imported.Reading.Written > 0, "The upload was read and nothing came of it.");

        // Every desk, not just the talkative ones: an interview reopened on the quiet agent is the
        // one that most needs to know what the file already says about that counter.
        Assert.All(imported.Draft.Agents, agent => Assert.True(
            agent.Known.Count > 0,
            $"Nothing was read off the configuration about '{agent.ID}'.\n{Read}"));
    }

    // Nobody said any of this. A reading that presents itself as the client's own words is the one
    // way this makes an interview worse than the empty state it replaces: a step holding it would
    // never think to put it to them, and a wrong one would stand for the rest of the domain.
    [Fact]
    public void Everything_read_says_it_was_read_and_not_said()
    {
        IEnumerable<KnownFact> everything = imported.Draft.Learned
            .Concat(imported.Draft.Agents.SelectMany(agent => agent.Known));

        Assert.All(everything, fact => Assert.True(
            fact.Inferred,
            $"'{fact.Fact}' is on record as something the client said, and the client has not spoken yet."));
    }

    // Filed under a subject in the trade's own words, because that is what a later step reads to
    // decide whether to fetch it at all: a desk whose subjects are unreadable is a desk nobody will
    // ever call RecallDesk on, and the memory might as well not be there.
    [Fact]
    public void Everything_read_is_filed_under_something_worth_reading()
    {
        IEnumerable<KnownFact> everything = imported.Draft.Learned
            .Concat(imported.Draft.Agents.SelectMany(agent => agent.Known));

        Assert.All(everything, fact =>
        {
            Assert.False(string.IsNullOrWhiteSpace(fact.Subject), $"'{fact.Fact}' is filed under nothing.");
            Assert.True(fact.Subject.Split(' ').Length <= 4, $"'{fact.Subject}' is a sentence, not a subject.");
        });
    }

    // The configuration read back is worth nothing to anybody: every step already opens holding it.
    // What is wanted is the business behind it — what they sell, who writes in, how the work goes —
    // which is the part a finished domain only implies and nobody ever wrote down.
    [Fact]
    public Task What_was_read_is_about_the_business_and_not_about_the_agents() => AssertDoesNotHoldAsync(
        "This text is mostly a description of software: it says what agents, tools or functions do, "
        + "rather than what the business does, who contacts it and how its work goes.",
        Read,
        "The reading gave back the configuration instead of the business behind it");

    private async Task AssertDoesNotHoldAsync(string proposition, string prose, string label)
    {
        JudgeVerdict verdict = await Judge.EvaluateAsync(proposition, prose);
        Assert.False(verdict.Holds, $"{label}: {verdict.Reason}\n\nWhat was read:\n{prose}");
    }
}
