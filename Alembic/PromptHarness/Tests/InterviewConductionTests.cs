using Distiller.Model;
using PromptHarness.Fixtures;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Tests Alembic's full interview conduct — every pass, for both agents Bistro Luna's map produces,
/// and the closing step that settles what passes between them — against the Draft the shared
/// <see cref="BistroLunaInterviewFixture"/> leaves behind.
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
        Assert.False(string.IsNullOrEmpty(interviewed.SavedTo), "The interview was not written down anywhere.");
        Assert.Null(driven.FinalState.Error);

        DomainDraft draft = interviewed.Draft;
        Assert.True(draft.Agents.Count == 2, $"Expected exactly two agents, found {draft.Agents.Count}.\n{driven}");

        // The two desks are found by the client's own words about them, so a domain where both
        // accessors land on the same agent would leave every later test judging the same prose twice
        // and reporting it under two different names. Asserted here rather than left to be noticed:
        // the last run's Doctrine failures all quoted the events desk, including the ones about the
        // front desk, and nothing said so.
        Assert.NotSame(interviewed.Reservations, interviewed.Events);

        foreach (AgentDraft agent in draft.Agents)
        {
            Assert.False(string.IsNullOrWhiteSpace(agent.Target), $"Agent '{agent.ID}' has no Target.\n{driven}");
            Assert.False(string.IsNullOrWhiteSpace(agent.Personality), $"Agent '{agent.ID}' has no Personality.\n{driven}");
            Assert.False(string.IsNullOrWhiteSpace(agent.Instructions), $"Agent '{agent.ID}' has no Instructions.\n{driven}");
            Assert.False(string.IsNullOrWhiteSpace(agent.Formatting), $"Agent '{agent.ID}' has no Formatting.\n{driven}");

            // Written by the Target pass from the scope it just fixed and never asked for. Every
            // agent carries one whether or not anybody consults it: it is published on the A2A card
            // and read by whoever holds a consult function for this desk, so an agent without one
            // is the only section of a domain whose absence nobody can notice from the inside.
            Assert.False(string.IsNullOrWhiteSpace(agent.ConsultMeFor), $"Agent '{agent.ID}' has no ConsultMeFor.\n{driven}");

            // Each desk was described as reaching something: the front desk checks and books, the
            // events desk reads a diary and takes an enquiry down. A toolkit with nothing in it
            // would be the AgentToolkit pass dropping what it was told rather than transcribing it.
            Assert.True(agent.Tools.Count >= 1, $"Agent '{agent.ID}' declared no tools at all.\n{driven}");
            Assert.All(agent.Tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Name), $"A tool of '{agent.ID}' has no Name.\n{driven}"));
            Assert.All(agent.Tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"Tool '{tool.Name}' has no Description.\n{driven}"));

            // Every accepted agent is Authored, since nothing here came from an upload — this is the
            // fact the migration report leans on to say "new" honestly.
            Assert.Equal(Provenance.Authored, agent.Origin);
        }

        // The fallback intent is the complement of a domain rather than a part of one: Morgana's
        // classifier carries it and describes it in its own prompt, so nothing authored here declares
        // it and an interview that produced one would be writing a desk nobody can answer for.
        Assert.DoesNotContain(draft.Intents, i =>
            string.Equals(i.Name, DomainDraft.FallbackIntent, StringComparison.OrdinalIgnoreCase));
    }

    // The closing step is the only pass that can settle this and the client's answer to it was a
    // fact about their own counter: whoever takes a table booking looks the back room up rather than
    // passing the call over. That is one edge in one direction and the interview's own state machine
    // is what writes it into the domain, which is why it is asserted on the committed Draft rather
    // than on anything the model said.
    [Fact]
    public void The_front_desk_leaves_the_interview_able_to_ask_the_events_desk()
    {
        DrivenInterview driven = interviewed.Driven;
        AgentDraft reservations = interviewed.Reservations;
        AgentDraft events = interviewed.Events;

        Assert.Contains(reservations.Code.Consults, colleague =>
            string.Equals(colleague.Intent, events.ID, StringComparison.OrdinalIgnoreCase)
            && colleague.Instance is null);

        // The client said the reverse never happens. An edge nobody asked for costs a colleague's
        // time while somebody waits, so proposing one is as much a defect as missing the one they
        // described — and this is also the only assertion that would catch a pass declaring
        // everybody a colleague of everybody.
        Assert.True(events.Code.Consults.Count == 0,
            $"The events desk declares {events.Code.Consults.Count} colleague(s) the client never described.\n{driven}");
    }

    // A step says what it adds to the agent before it asks anything, and it says it as its own
    // paragraph: the placing sentence and the question are two different things to read and the
    // screen shows them in two different voices. Run into one block they are a wall of text in
    // which nothing says which sentence is the one the client has to answer.
    [Fact]
    public void Every_step_places_itself_above_the_question_it_asks()
    {
        DrivenInterview driven = interviewed.Driven;

        List<DrivenExchange> openings = driven.Exchanges
            .Where((exchange, index) => index == 0 || driven.Exchanges[index - 1].Pass != exchange.Pass)
            .ToList();

        List<DrivenExchange> run = openings
            .Where(exchange => !exchange.Question.Replace("\r\n", "\n").Contains("\n\n"))
            .ToList();

        Assert.True(run.Count == 0,
            $"{run.Count} step(s) opened with the placing sentence run into the question:\n"
            + string.Join("\n", run.Select(exchange => $"[{exchange.Pass}] {exchange.Question}"))
            + $"\n\n{driven}");
    }

    // A worked answer standing in the box is the only thing on the screen that says how much of an
    // answer this question is worth: a client who cannot see its cut writes a word and a domain
    // mapped out of single words is one whose holes first show at the emit. Two questions are
    // exempt and both because nothing is missing from the screen — one carrying the button already
    // shows the answer that adds nothing and the very first question of all is asked of somebody
    // nothing is known about yet, where the page's own house example stands in.
    [Fact]
    public void Every_open_question_arrives_with_a_worked_example()
    {
        DrivenInterview driven = interviewed.Driven;

        List<DrivenExchange> bare = driven.Exchanges
            .Skip(1)
            .Where(exchange => exchange.Choice is null && string.IsNullOrWhiteSpace(exchange.Example))
            .ToList();

        Assert.True(bare.Count == 0,
            $"{bare.Count} open question(s) were asked over an empty box:\n"
            + string.Join('\n', bare.Select(exchange => $"[{exchange.Pass}] {exchange.Question}"))
            + $"\n\n{driven}");
    }
}
