using Distiller.Interfaces;
using Distiller.Model;
using Distiller.Services;
using Microsoft.Extensions.DependencyInjection;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The rules about colleagues that are decidable by reading a domain: what the deterministic
/// validation refuses, and what the tool the closing step writes edges with refuses before it.
/// </summary>
/// <remarks>
/// Nothing here calls a model, so the whole class runs on a working copy with no provider
/// configured at all and costs nothing to run on every change. That is the point of it: every rule
/// below restates one the framework enforces at a client's startup — an agent naming itself, an
/// intent no agent handles, a partner with no name — and the value of the duplication is entirely
/// in when it is read. Morgana's exception arrives after the client has packaged, deployed and run.
/// <para>
/// The two halves are the same rule at two distances. <c>DraftValidationService</c> answers for the
/// domain as it stands, wherever the edge came from — an upload, the emit page, the interview. The
/// tool answers in the turn, to the model, so a pass that proposes an impossible edge is told why
/// and corrects itself before the client sees anything.
/// </para>
/// </remarks>
[Trait("Stage", "Rules")]
public sealed class ConsultationRulesTests
{
    private readonly AlembicHostFixture fixture;

    public ConsultationRulesTests(AlembicHostFixture fixture) => this.fixture = fixture;

    // ---- What the domain is held to ------------------------------------------------------------

    [Fact]
    public void An_agent_naming_itself_as_a_colleague_is_an_error()
    {
        // Startup refuses the pairing outright: an agent cannot be its own second opinion.
        IReadOnlyList<ValidationFinding> findings = Validate(
            Domain(Agent("reservations", new Morgana.AI.Records.PeerReference("reservations")), Agent("events")));

        Assert.Contains(findings, f => f.Severity == FindingSeverity.Error && f.Message.Contains("declares itself as a colleague"));
    }

    [Fact]
    public void A_colleague_no_agent_of_the_domain_handles_is_an_error()
    {
        // A consultation is resolved when the agent is created, so a local name nothing answers is
        // startup-fatal rather than a colleague that quietly never appears.
        IReadOnlyList<ValidationFinding> findings = Validate(
            Domain(Agent("reservations", new Morgana.AI.Records.PeerReference("catering")), Agent("events")));

        Assert.Contains(findings, f => f.Severity == FindingSeverity.Error && f.Message.Contains("which no agent of this domain handles"));
    }

    [Fact]
    public void An_edge_onto_an_agent_that_consults_is_reported_as_a_narrower_reach()
    {
        // Legal, and narrower than it looks: while a colleague answers, the framework withholds its
        // own peer functions, so the second hop never happens and the far end's answer is not part
        // of what comes back.
        IReadOnlyList<ValidationFinding> findings = Validate(
            Domain(
                Agent("reservations", new Morgana.AI.Records.PeerReference("events")),
                Agent("events", new Morgana.AI.Records.PeerReference("kitchen")),
                Agent("kitchen")));

        Assert.Contains(findings, f => f.Severity == FindingSeverity.Warning && f.Message.Contains("consults a colleague of its own"));
    }

    [Fact]
    public void A_colleague_at_a_partner_is_left_to_that_partner_to_answer_for()
    {
        // Its intent is answered by a domain this installation cannot see, so the only thing checked
        // on this side is that the partner was named at all — an unknown intent there is not a
        // defect, it is a question for that installation's own card.
        IReadOnlyList<ValidationFinding> findings = Validate(
            Domain(Agent("reservations", new Morgana.AI.Records.PeerReference("shipping", "acme"))));

        Assert.DoesNotContain(findings, f => f.Message.Contains("shipping"));
    }

    [Fact]
    public void A_colleague_at_a_partner_with_no_name_is_an_error()
    {
        // The name is matched against an entry under Morgana:AgentToAgent:Partners and a blank one
        // matches nothing, so startup refuses the agent that declares it.
        IReadOnlyList<ValidationFinding> findings = Validate(
            Domain(Agent("reservations", new Morgana.AI.Records.PeerReference("shipping", "  "))));

        Assert.Contains(findings, f => f.Severity == FindingSeverity.Error && f.Message.Contains("at a system with no name"));
    }

    // ---- What the tool refuses in the turn ------------------------------------------------------

    [Fact]
    public void The_tool_refuses_an_agent_consulting_itself()
    {
        string answer = Tools(out _).DeclareConsultation("reservations", "reservations", "[INSTRUCTIONS] Anything.");

        Assert.Contains("cannot consult itself", answer);
    }

    [Fact]
    public void The_tool_refuses_a_colleague_the_domain_does_not_hold()
    {
        string answer = Tools(out _).DeclareConsultation("reservations", "catering", "[INSTRUCTIONS] Anything.");

        Assert.Contains("no agent of this domain answers", answer);
    }

    [Fact]
    public void The_tool_refuses_an_edge_that_leaves_the_prose_as_it_was()
    {
        // The defect the closing step exists against: an agent handed a colleague while its own
        // prose still says the subject belongs elsewhere is given two orders, and the flat one wins.
        string answer = Tools(out InterviewState state).DeclareConsultation("reservations", "events", "   ");

        Assert.Contains("needs its Instructions rewritten in the same call", answer);
        Assert.Empty(state.Colleagues);
    }

    [Fact]
    public void An_accepted_edge_carries_the_rewritten_boundary_with_it()
    {
        InterviewTools tools = Tools(out InterviewState state);

        string answer = tools.DeclareConsultation(
            "reservations", "events",
            "The dining room's tables are on this desk's own books; the back room's diary is not.");

        Assert.Contains("may now ask", answer);

        ConsultationDraft edge = Assert.Single(state.Colleagues);
        Assert.Equal("reservations", edge.Asking);
        Assert.Equal("events", edge.Asked);

        // Fenced the way every other authored section is: the label is Morgana's own lexicon and is
        // put on in code, never asked of the model.
        Assert.StartsWith("[INSTRUCTIONS]", edge.AskingInstructions?.TrimStart() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("back room's diary", edge.AskingInstructions!);
    }

    // ---- The domains these read ------------------------------------------------------------------

    /// <summary>The deterministic findings over one domain — no model asked, none would help.</summary>
    private static IReadOnlyList<ValidationFinding> Validate(DomainDraft draft) =>
        new DraftValidationService().Validate(draft);

    /// <summary>
    /// The interview's own toolset, bound to a two-desk domain and an interview standing on the
    /// closing step.
    /// </summary>
    /// <remarks>
    /// Built over Alembic's own service graph rather than over stand-ins, because what is under test
    /// is the tool a real pass calls — but nothing it does here reaches a model: an edge is refused
    /// or recorded by reading the domain in hand.
    /// </remarks>
    private InterviewTools Tools(out InterviewState state)
    {
        IServiceScope scope = fixture.NewScope();
        IDraftStateService draftState = scope.ServiceProvider.GetRequiredService<IDraftStateService>();

        draftState.Set(Domain(Agent("reservations"), Agent("events")));

        state = new InterviewState { Pass = InterviewStep.DomainColleagues };

        return new InterviewTools(
            state,
            draftState,
            scope.ServiceProvider.GetRequiredService<IDraftValidationService>(),
            scope.ServiceProvider.GetRequiredService<IRecapService>());
    }

    /// <summary>One agent, written enough to be a domain's agent and no more.</summary>
    private static AgentDraft Agent(string id, params Morgana.AI.Records.PeerReference[] consults)
    {
        AgentDraft agent = new()
        {
            ID = id,
            Target = $"It answers for {id} and nothing else.",
            ConsultMeFor = $"Whatever falls to {id}.",
            Instructions = $"It works through {id} plainly.",
            Formatting = "It answers in short prose.",
            Origin = Provenance.Authored
        };

        agent.Code.Consults.AddRange(consults);

        return agent;
    }

    /// <summary>The agents as a domain, each with the intent that routes to it.</summary>
    private static DomainDraft Domain(params AgentDraft[] agents) => new()
    {
        Intents =
        [
            .. agents.Select(a => new IntentDraft
            {
                Name = a.ID,
                Description = $"Anything about {a.ID}.",
                Label = a.ID,
                DefaultValue = $"I have a question about {a.ID}."
            })
        ],
        Agents = [.. agents]
    };
}
