using Distiller2.Interfaces;
using Distiller2.Model;
using PromptHarness.Fixtures;
using PromptHarness.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Judges the authored prose against Alembic's own doctrine, one section at a time and then across
/// sections — independently of Alembic's own self-check, which is the same conducting session
/// re-reading what it just wrote and, on a live run against this exact fixture, followed a client's
/// explicit request straight past the rule it had just been taught.
/// </summary>
/// <remarks>
/// Every rule judged here is one CLAUDE.md already states as binding on every authored agent — this
/// class does not invent doctrine, it makes doctrine already written down independently checkable.
/// A proposition that fails here is either a real regression or a proposition that no longer
/// reflects the doctrine and either way is worth seeing named, not folded into a single broad
/// "does this look right" question a judge could pass by being right about only one section.
/// </remarks>
[Collection(BistroLunaCollection.Name)]
[Trait("Stage", "Finalization")]
public sealed class DoctrineTests
{
    private readonly BistroLunaInterviewFixture interviewed;
    /// <summary>
    /// The front desk. Every proposition below about slots, bookings and window seats is about the
    /// agent the client described in those words, which is not necessarily the first entry the
    /// mapper wrote.
    /// </summary>
    private AgentDraft Agent => interviewed.Reservations;

    /// <summary>The events desk: the colleague the front desk leaves the interview able to ask.</summary>
    private AgentDraft Colleague => interviewed.Events;

    public DoctrineTests(BistroLunaInterviewFixture interviewed) => this.interviewed = interviewed;

    private Judge Judge => interviewed.Services.GetRequiredService<Judge>();
    private IRecapService Recap => interviewed.Services.GetRequiredService<IRecapService>();

    /// <summary>Asserts a proposition must hold, with the judged prose in the failure message.</summary>
    private async Task AssertHoldsAsync(string proposition, string prose, string label)
    {
        JudgeVerdict verdict = await Judge.EvaluateAsync(proposition, prose);
        Assert.True(verdict.Holds, $"{label}: {verdict.Reason}\n\nJudged prose:\n{prose}\n\n{interviewed.Driven}");
    }

    /// <summary>Asserts a proposition must NOT hold, with the judged prose in the failure message.</summary>
    private async Task AssertDoesNotHoldAsync(string proposition, string prose, string label)
    {
        JudgeVerdict verdict = await Judge.EvaluateAsync(proposition, prose);
        Assert.False(verdict.Holds, $"{label}: {verdict.Reason}\n\nJudged prose:\n{prose}\n\n{interviewed.Driven}");
    }

    // ---- Target ----------------------------------------------------------------------------

    // Target's own job, per CLAUDE.md's doctrine table, is what the agent does and does not do —
    // existentially, not who it is: naming which facet of Morgana this agent is belongs to
    // Personality alone ("Personality names which facet she is here") and testing Target for a
    // named persona was testing it against a job it was never given. What Target must still never
    // do is claim to BE one of the generic archetypes CLAUDE.md rules out — a virtual assistant, a
    // chatbot, a helpful bot, neutral customer service staff — which is the actual anti-pattern
    // this checks for, independently of whether Target happens to name Morgana at all.
    [Fact]
    public Task Target_never_claims_to_be_a_generic_assistant() => AssertDoesNotHoldAsync(
        "This text explicitly describes the agent as being a virtual assistant, a chatbot, a helpful "
        + "bot, or neutral, faceless customer service staff.",
        Agent.Target ?? string.Empty,
        "Target claims to be a generic assistant archetype");

    [Fact]
    public Task Target_states_an_explicit_boundary() => AssertHoldsAsync(
        "This text explicitly states at least one thing the agent must never do or is not able to "
        + "do, in addition to describing what it is for.",
        Agent.Target ?? string.Empty,
        "Target states no explicit boundary");

    // A boundary is a fact about this desk's own books — the subject it does not keep — and never a
    // direction to another one. Judged on the events desk, which gained no colleague in the closing
    // step, so its Target is the one the interview wrote and nothing has been back to repair it.
    [Fact]
    public Task Target_states_its_boundary_without_pointing_anywhere() => AssertDoesNotHoldAsync(
        "This text says where a subject it does not handle should go instead, naming another desk, "
        + "office, department, team, colleague, number or agent.",
        Colleague.Target ?? string.Empty,
        "The Target points the customer somewhere else instead of saying what it does not keep");

    // ---- Composed prompt ---------------------------------------------------------------------

    // Self-awareness of being Morgana is real doctrine — "An authored agent is one agent of
    // Morgana, never a separate creature" — but it is a property of what the agent's own model
    // actually reads and that is never Agent.Target alone. IRecapService.ComposeAsync produces the
    // same two layers the running agent gets: Morgana's own framework layer (her Personality, her
    // Target, resolved live from morgana.json) UNDER the domain layer this interview wrote. The
    // framework layer is where her identity lives; asking the bare domain Target to carry it too,
    // as the previous version of this test did, was demanding the same fact be stated twice and
    // failing a correctly-scoped Target for not doing the framework layer's job.
    [Fact]
    public async Task Composed_prompt_reads_as_self_aware_of_being_Morgana()
    {
        AgentRecap recap = await Recap.ComposeAsync(Agent);

        await AssertHoldsAsync(
            "This text presents the agent as one agent of Morgana — part of a larger assistant "
            + "named Morgana, not a standalone or separate creature of its own — rather than as "
            + "generic, unaffiliated software with no such identity.",
            recap.SystemPrompt,
            "Composed prompt shows no self-awareness of being an agent of Morgana");
    }

    // ---- Personality -------------------------------------------------------------------------

    [Fact]
    public Task Personality_is_voice_not_instruction() => AssertDoesNotHoldAsync(
        "This text tells the agent what actions to perform or what task to accomplish, rather than "
        + "describing how it sounds and comes across to whoever it is speaking with.",
        Agent.Personality ?? string.Empty,
        "Personality instructs behaviour instead of describing voice");

    [Fact]
    public Task Personality_is_prose_not_a_bare_adjective_list() => AssertHoldsAsync(
        "This text is written as connected prose describing a persona, not merely a comma-separated "
        + "list of adjectives with nothing else.",
        Agent.Personality ?? string.Empty,
        "Personality reads as a bare adjective list rather than prose");

    // ---- Instructions --------------------------------------------------------------------------

    [Fact]
    public Task Instructions_state_no_generic_framework_rule() => AssertDoesNotHoldAsync(
        "This text states a general rule about markdown formatting, about the mechanics of how "
        + "quick-reply buttons work, or about session or turn continuation — a rule that would be "
        + "equally true of any agent regardless of domain, rather than something specific to this "
        + "particular business.",
        Agent.Instructions ?? string.Empty,
        "Instructions restate a framework-owned rule instead of a domain-specific one");

    // ---- Formatting ----------------------------------------------------------------------------

    [Fact]
    public Task Formatting_never_offers_one_button_per_open_slot() => AssertDoesNotHoldAsync(
        "This text tells the agent to offer a separate selectable button for each individual open "
        + "time slot returned by an availability check (one button per slot), rather than describing "
        + "the open slots in prose.",
        Agent.Formatting ?? string.Empty,
        "Formatting commits the one-button-per-open-slot anti-pattern");

    [Fact]
    public Task Formatting_requires_an_explicit_confirmation_before_booking() => AssertHoldsAsync(
        "This text requires the customer to make a clear, explicit yes/no choice before the "
        + "reservation is actually placed — not an inferred or assumed confirmation.",
        (Agent.Instructions ?? string.Empty) + "\n\n" + (Agent.Formatting ?? string.Empty),
        "The required confirmation gate was not found");

    // ---- ConsultMeFor ---------------------------------------------------------------------------

    // The one section whose reader is another agent. It never enters this agent's own prompt: it is
    // published on the A2A card and appended to the prompt of whoever declares this desk a
    // colleague. Judged on the events desk because that is the one somebody actually consults, so a
    // defect here is paid for every time the front desk asks.

    [Fact]
    public Task ConsultMeFor_states_the_territory_this_desk_answers_for() => AssertHoldsAsync(
        "This text states what subjects or matters this desk is responsible for and can answer "
        + "about — its territory — rather than being empty, vague, or about something else.",
        Colleague.ConsultMeFor ?? string.Empty,
        "ConsultMeFor states no territory");

    [Fact]
    public Task ConsultMeFor_is_not_an_inventory_of_what_the_agent_can_do() => AssertDoesNotHoldAsync(
        "This text enumerates the specific operations, functions, tools or steps the agent can "
        + "perform, as a list of capabilities.",
        Colleague.ConsultMeFor ?? string.Empty,
        "ConsultMeFor reads as an inventory of functions rather than a territory");

    // Stating what falls to this desk is the section's whole job, so a proposition that also
    // forbade saying when a question belongs here was forbidding the content along with the rule:
    // it failed a correctly-scoped statement for naming its own subjects. What is genuinely
    // framework-owned is the conduct of the consultation, and that is what this holds it to.
    [Fact]
    public Task ConsultMeFor_states_no_rule_about_consulting() => AssertDoesNotHoldAsync(
        "This text states a rule about how a consultation is to be conducted — how briefly to ask, "
        + "in what form, what will come back, what the asker should do with the answer, or that the "
        + "answer is to be given in the asker's own voice.",
        Colleague.ConsultMeFor ?? string.Empty,
        "ConsultMeFor restates a framework-owned rule about consulting");

    // ---- The boundary that gained a colleague ----------------------------------------------------

    // The client's closing answer was that the front desk looks the back room up itself rather than
    // passing the call over, so the hand-off its own Instructions carried has to be gone. An edge
    // whose prose still refuses the subject is the defect the closing step exists against: the agent
    // is handed the colleague as a function and told in the same prompt not to touch the subject,
    // and the flat imperative is the one it obeys.

    [Fact]
    public Task The_front_desk_no_longer_sends_the_customer_to_another_desk() => AssertDoesNotHoldAsync(
        "This text tells the agent to send, refer, redirect or hand the customer over to another "
        + "desk, office, department, number or team about private events or the back room.",
        Agent.Instructions ?? string.Empty,
        "The boundary still sends the customer away for a subject a colleague answers");

    // The framework appends the colleague's own ConsultMeFor to this agent's prompt, so a copy of
    // the colleague's territory here is the same contradiction from the other side — and stale the
    // day the colleague restates its own scope.
    [Fact]
    public Task The_front_desk_does_not_restate_what_its_colleague_is_for() => AssertDoesNotHoldAsync(
        "This text describes what another desk, office or agent is responsible for, names one as a "
        + "colleague, or states that this agent can ask another agent or desk for help.",
        Agent.Instructions ?? string.Empty,
        "Instructions restate the colleague's territory or the machinery of consulting");

    // ---- What the client was actually asked ---------------------------------------------------

    /// <summary>Everything one pass put to the client, as one body of text for a single verdict.</summary>
    private string Asked(InterviewStep pass) => string.Join(
        "\n\n",
        interviewed.Driven.Exchanges.Where(exchange => exchange.Pass == pass).Select(exchange => exchange.Question));

    // The toolkit is written out of what their own people already do — which screen they open, what
    // they type in, who they send it on to. A client asked instead what an agent ought to be able to
    // check is being asked to design software and answers with what they imagine software does, so
    // the toolkit ends up describing a system nobody has rather than the counter they work at.
    [Fact]
    public Task The_toolkit_pass_asks_about_their_own_counter() => AssertDoesNotHoldAsync(
        "Any question in this text asks the reader what a system, an agent, an assistant or a bot "
        + "should be able to do, look up, check or handle, rather than asking what the reader and "
        + "their own staff do, open or look at.",
        Asked(InterviewStep.AgentToolkit),
        "The toolkit pass asked the client to design software instead of describing their work");

    // By the time the interview reaches an agent's Instructions it has been told what the place
    // sells, who writes in and what they open to answer. A question that could have been put to any
    // business on earth is one asked by a step that read none of it, which is what makes a client
    // feel they are filling in a form rather than being interviewed by somebody who is listening.
    [Fact]
    public Task The_later_passes_ask_about_this_business_and_not_any_business() => AssertDoesNotHoldAsync(
        "Every question in this text is generic: none of them mentions anything particular to the "
        + "business being interviewed — no detail of what it sells, who contacts it or how it works.",
        Asked(InterviewStep.AgentInstructions),
        "The instructions pass asked questions that fit any business at all");

    /// <summary>Everything the interview wrote down about the client's work, as one body of text.</summary>
    private string Learned => string.Join(
        "\n",
        interviewed.Draft.Learned
            .Concat(interviewed.Draft.Agents.SelectMany(agent => agent.Known))
            .Select(fact => $"- {fact.Subject}: {fact.Fact}"));

    // What is written down has to be the business, not the configuration in other words. A record
    // that says what an agent handles is a second copy of a section: two statements of one thing,
    // free to drift apart, and worth nothing to a step that already opens holding the section
    // itself. What earns its place is what no section has a reader for — how the work actually goes.
    [Fact]
    public Task What_was_written_down_is_their_work_and_not_the_configuration() => AssertDoesNotHoldAsync(
        "This text is mostly a description of the software: it says what agents or tools do, or "
        + "what each desk handles, rather than how the business itself works.",
        Learned,
        "The interview wrote the configuration back into its own notes");

    /// <summary>Everything the client was shown across the whole interview, question and placing alike.</summary>
    private string Shown => string.Join(
        "\n",
        interviewed.Driven.Exchanges.SelectMany(exchange =>
            new[] { exchange.Placing, exchange.Question }.Where(said => !string.IsNullOrWhiteSpace(said))));

    // Whether a word belongs to the client's world is a question about meaning, so it is put to a
    // judge rather than to a list: a list catches 'channel' and lets through 'another area', 'a
    // different flow', 'the other side', which lose the client in exactly the same way. What is
    // being protected is that the person reading understood the sentence they were asked to confirm.
    [Fact]
    public Task Nothing_shown_to_the_client_names_something_only_the_software_has() => AssertDoesNotHoldAsync(
        "Somewhere in this text the reader — the owner of the business being interviewed, who has "
        + "never been shown how any of this is built — is told about a thing that exists only in "
        + "software: a place requests go to, a part of a system, something handling a subject, named "
        + "in words that are not how a shopkeeper would describe their own staff, counters or "
        + "suppliers.",
        Shown,
        "The client was shown something that exists only in the machinery");

    // An example is an answer to the question, never the question again with 'you' turned into 'we'.
    // One that says what the question already said costs the client the turn: they read the same
    // sentence twice, press past it and the pass ends holding its own words. Judged rather than
    // measured — a restatement dressed in synonyms is the same defect and no counting finds it.
    [Fact]
    public Task No_example_merely_gives_its_own_question_back() => AssertDoesNotHoldAsync(
        "In this text, one or more of the worked answers says essentially what its own question said, "
        + "only rephrased as a statement, adding no detail of the business that the question did not "
        + "already contain.",
        string.Join("\n\n", interviewed.Driven.Exchanges
            .Where(exchange => exchange.Example is not null)
            .Select(exchange => $"Question: {exchange.Question}\nIn the box: {exchange.Example}")),
        "A worked example gave its own question back");

    // The voice is the one thing the client cannot dictate: nobody has a sentence ready about how
    // their own people come across, and asked to produce one they invent a character for a machine.
    // What they can do is recognise their own counter in words put in front of them. Asked instead
    // what voice they would like to hear, they are being asked about something that does not exist —
    // an agent of Morgana writes and is never heard — and the answer describes an imagined robot.
    [Fact]
    public Task The_voice_pass_asks_them_to_recognise_their_counter() => AssertDoesNotHoldAsync(
        "A question in this text asks the reader what voice, sound or tone they would like to hear, "
        + "or asks them to decide what an assistant, agent or system should be like, rather than "
        + "asking what the people who already work at their counter are like with a customer.",
        Asked(InterviewStep.AgentPersonality),
        "The voice pass asked them to commission a character instead of recognising their own people");

    /// <summary>Every sentence that placed a step, across the whole interview.</summary>
    private string Placings => string.Join(
        "\n",
        interviewed.Driven.Exchanges.Select(exchange => exchange.Placing).Where(said => said is not null));

    // A step places itself by saying what the client will be able to hand over once it is settled,
    // in what their own work is made of. The prose explaining to the pass why the stage exists is
    // written for the pass, and recycling it at the client produces the sentence that could open any
    // step of any interview for any business — which places nothing and reads as filler.
    [Fact]
    public Task Each_step_places_itself_in_the_client_own_work() => AssertDoesNotHoldAsync(
        "One or more of these sentences is generic or promotional: it describes what software or a "
        + "system becomes able to do, explains why this stage of the process exists, or suggests that "
        + "what came before it was preliminary and things only now become real — rather than naming "
        + "something particular to this business that the reader will be able to hand over.",
        Placings,
        "A step placed itself with a sentence that would fit any business at all");

    // ---- Cross-section coherence -----------------------------------------------------------

    [Fact]
    public Task Formatting_does_not_contradict_instructions()
    {
        string combined =
            $"INSTRUCTIONS:\n{Agent.Instructions}\n\nFORMATTING:\n{Agent.Formatting}";

        return AssertDoesNotHoldAsync(
            "The text under FORMATTING describes presenting information, or behaving, in a way that "
            + "is inconsistent with or contradicts what the text under INSTRUCTIONS says.",
            combined,
            "Formatting contradicts Instructions");
    }

    [Fact]
    public Task Agent_never_exceeds_the_boundaries_its_target_declares()
    {
        string combined =
            $"TARGET (states the agent's boundaries):\n{Agent.Target}\n\n" +
            $"INSTRUCTIONS:\n{Agent.Instructions}\n\nFORMATTING:\n{Agent.Formatting}";

        // Tied to this fixture's own Target on purpose — Bistro Luna's script establishes these
        // three specific exclusions, so this is exactly what a domain's own boundary should be
        // checked against, not a generic rule invented here.
        return AssertDoesNotHoldAsync(
            "The text under INSTRUCTIONS or FORMATTING describes the agent taking a payment, "
            + "modifying the seating plan, or guaranteeing a specific table type such as a window "
            + "seat — any of which the text under TARGET says the agent must never do.",
            combined,
            "Instructions or Formatting exceed a boundary Target declares");
    }
}
