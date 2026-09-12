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

    // What the client says about their trade is spent the moment the turn ends unless it is written
    // down: every pass is a fresh session reading the configuration, which holds what was decided
    // and nothing about the shop it was decided for. An interview that recorded nothing has three
    // passes ahead of it asking a baker what a system ought to be able to check.
    [Fact]
    public void The_interview_writes_down_what_it_is_told_about_the_business()
    {
        DrivenInterview driven = interviewed.Driven;
        DomainDraft draft = interviewed.Draft;

        Assert.All(draft.Agents, agent => Assert.True(agent.Known.Count > 0,
            $"Agent '{agent.ID}' came out of the interview with nothing written down about the work it does.\n{driven}"));
    }

    // A step says what it adds to the agent before it asks anything: the placing sentence and the
    // question are two different things to read and the screen draws them in two different voices.
    // A step that lands with nothing but its question leaves the client to work out what this
    // screen is for from the question alone, which is the one thing it cannot say.
    [Fact]
    public void Every_step_places_itself_above_the_question_it_asks()
    {
        DrivenInterview driven = interviewed.Driven;

        List<DrivenExchange> openings = driven.Exchanges
            .Where((exchange, index) => index == 0 || driven.Exchanges[index - 1].Pass != exchange.Pass)
            .ToList();

        List<DrivenExchange> run = openings
            .Where(exchange => string.IsNullOrWhiteSpace(exchange.Placing))
            .ToList();

        Assert.True(run.Count == 0,
            $"{run.Count} step(s) opened with the placing sentence run into the question:\n"
            + string.Join("\n", run.Select(exchange => $"[{exchange.Pass}] {exchange.Question}"))
            + $"\n\n{driven}");
    }

    // Every pass states back what it wrote and asks whether that is right, and that turn carries the
    // button: its whole question is whether the client agrees, so somebody with nothing to object to
    // should not have to type a word meaning yes. A pass that never offers one has made the client
    // pay a typed sentence for every step of the interview. What is checked is the button's own
    // presence, which is a fact about the turn rather than a reading of its words.
    [Fact]
    public void Every_pass_offers_the_button_on_the_turn_it_states_its_work_back()
    {
        DrivenInterview driven = interviewed.Driven;

        List<InterviewStep> silent = driven.Exchanges
            .GroupBy(exchange => exchange.Pass)
            .Where(pass => pass.All(exchange => exchange.Choice is null))
            .Select(pass => pass.Key)
            .ToList();

        Assert.True(silent.Count == 0,
            $"{silent.Count} pass(es) ran to the end without once offering the button: "
            + string.Join(", ", silent)
            + $"\n\n{driven}");
    }

    // The prose a client approves is shown to them whole, apart from the sentence introducing it:
    // they are agreeing to exact words, and words folded into somebody else's sentence are words
    // nobody can see the edges of. Every pass writes something and asks whether it is right, so
    // every pass has a turn that shows it.
    [Fact]
    public void Every_pass_shows_the_client_the_words_it_wrote()
    {
        DrivenInterview driven = interviewed.Driven;

        List<InterviewStep> unshown = driven.Exchanges
            .GroupBy(exchange => exchange.Pass)
            .Where(pass => pass.All(exchange => string.IsNullOrWhiteSpace(exchange.Quoted)))
            .Select(pass => pass.Key)
            .ToList();

        Assert.True(unshown.Count == 0,
            $"{unshown.Count} pass(es) asked the client to approve prose they were never shown: "
            + string.Join(", ", unshown)
            + $"\n\n{driven}");
    }

    // The page draws what the pass writes exactly as it arrives, so a word it meant to stress reaches
    // the client wearing two asterisks. This is the one place characters themselves are the defect —
    // nothing here is a judgement about language, an asterisk is simply the wrong thing to have on
    // the screen — so it is caught by looking, and everything the client reads is looked at.
    [Fact]
    public void Nothing_the_client_reads_arrives_wearing_markup()
    {
        DrivenInterview driven = interviewed.Driven;
        string[] marks = ["**", "__", "`", "##"];

        List<string> marked = driven.Exchanges
            .SelectMany(exchange => new[] { exchange.Question, exchange.Placing, exchange.Example }
                .Where(written => written is not null && marks.Any(mark => written.Contains(mark, StringComparison.Ordinal)))
                .Select(written => $"[{exchange.Pass}] {written}"))
            .ToList();

        Assert.True(marked.Count == 0,
            $"{marked.Count} thing(s) reached the client with markup in them:\n"
            + string.Join("\n", marked)
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
