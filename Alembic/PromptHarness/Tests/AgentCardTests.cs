using Distiller.Interfaces;
using Distiller.Model;
using Microsoft.Extensions.DependencyInjection;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The card an agent presents to whoever might consult it, checked as far as it is decidable
/// without a model.
/// </summary>
/// <remarks>
/// The card is the whole of what a colleague weighing a question is given: one description and, for
/// a stranger outside this installation, the skills. What can be settled by reading is which of the
/// two descriptions stands there — the phrase the classifier routes on, until the territory is
/// settled — and what that sentence is made of. Whether it names the right territory is a judgement
/// and lives with the coherence pass.
/// <para>
/// Free of any model, so it runs on every change rather than on the runs somebody pays for.
/// </para>
/// </remarks>
[Trait("Stage", "Rules")]
public sealed class AgentCardTests
{
    private readonly AlembicHostFixture fixture;

    public AgentCardTests(AlembicHostFixture fixture) => this.fixture = fixture;

    /// <summary>The shipped domain, as a client's own upload reaches Alembic.</summary>
    private const string DomainFile = "examples-agents.json";

    /// <summary>A desk whose territory is written the way the doctrine asks for.</summary>
    private static (IntentDraft Intent, AgentDraft Agent) Desk() =>
        (new IntentDraft { Name = "reservations", Description = "Requests to check whether a table is free and to book one." },
         new AgentDraft
         {
             ID = "reservations",
             ConsultMeFor = "[CONSULT ME FOR] The state of the table diary — what the dining room can still hold on a day and what it cannot.",
             Tools = [new ToolDraft { Name = "CheckTableAvailability", Description = "Reads the diary for a day." }]
         });

    [Fact]
    public void The_card_presents_the_territory_its_agent_wrote()
    {
        (IntentDraft intent, AgentDraft agent) = Desk();

        string card = CardProjection.Render(intent, agent);

        Assert.Contains("table diary", card, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(intent.Description!, card, StringComparison.OrdinalIgnoreCase);
    }

    // Where the card starts. Until the territory is settled the desk is described by the phrase the
    // classifier routes on, which says which utterances land here rather than what this desk answers
    // for — so the interview is not finished with it and validation says so.
    [Fact]
    public void A_desk_that_has_not_said_what_it_answers_for_is_still_described_by_what_routes_to_it()
    {
        (IntentDraft intent, AgentDraft agent) = Desk();
        agent.ConsultMeFor = null;

        Assert.Contains(intent.Description!, CardProjection.Render(intent, agent), StringComparison.Ordinal);

        Assert.Contains(Findings(intent, agent), finding =>
            finding.Message.Contains("nothing to say to a colleague", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_card_advertises_one_skill_per_tool_of_its_agent()
    {
        (IntentDraft intent, AgentDraft agent) = Desk();

        string card = CardProjection.Render(intent, agent);

        Assert.Contains("CheckTableAvailability", card, StringComparison.Ordinal);
        Assert.Contains("Reads the diary for a day.", card, StringComparison.Ordinal);
    }

    // An MCP-only agent is a legal configuration, so the card says so rather than advertising an
    // emptiness a reader would take for a broken projection.
    [Fact]
    public void A_desk_with_no_tool_of_its_own_advertises_no_skill()
    {
        (IntentDraft intent, AgentDraft agent) = Desk();
        agent.Tools = [];

        Assert.Contains("no tool of its own", CardProjection.Render(intent, agent), StringComparison.Ordinal);
    }

    /// <summary>A territory written as the functions the desk performs is an inventory, and reported.</summary>
    /// <remarks>
    /// A colleague handed a list of functions rules its question out instead of asking it, which is
    /// the one failure of this section nobody inside the domain can notice: the asking agent simply
    /// never asks and answers "go to them" on its own.
    /// </remarks>
    [Fact]
    public void A_territory_naming_its_own_tools_is_reported()
    {
        (IntentDraft intent, AgentDraft agent) = Desk();
        agent.ConsultMeFor = "[CONSULT ME FOR] CheckTableAvailability for a day, then a booking is placed.";

        Assert.Contains(Findings(intent, agent), finding =>
            finding.Message.Contains("names its own tool", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_territory_that_is_only_the_routing_phrase_is_reported()
    {
        (IntentDraft intent, AgentDraft agent) = Desk();
        agent.ConsultMeFor = $"[CONSULT ME FOR] {intent.Description}";

        Assert.Contains(Findings(intent, agent), finding =>
            finding.Message.Contains("its own routing description", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_territory_of_its_own_words_is_reported_as_nothing()
    {
        (IntentDraft intent, AgentDraft agent) = Desk();

        Assert.DoesNotContain(Findings(intent, agent), finding =>
            finding.Where.Contains("reservations", StringComparison.OrdinalIgnoreCase)
            && (finding.Message.Contains("colleague", StringComparison.OrdinalIgnoreCase)
                || finding.Message.Contains("territory", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Every agent of the shipped domain presents a card a colleague can read.</summary>
    /// <remarks>
    /// The one place these checks meet prose nobody wrote for them: <c>Examples</c> is the domain the
    /// repository builds and demonstrates, so a territory going stale there is a defect a client
    /// meets on their first upload.
    /// </remarks>
    [Fact]
    public async Task Every_agent_of_the_shipped_domain_presents_a_readable_card()
    {
        using IServiceScope scope = fixture.NewScope();

        await using FileStream file = File.OpenRead(DomainFile);
        DraftImportResult imported = await scope.ServiceProvider
            .GetRequiredService<IDraftImportService>()
            .ImportAsync(file, DomainFile);

        DomainDraft draft = imported.Draft
                            ?? throw new InvalidOperationException($"{DomainFile} did not import: {imported.Error}");

        Assert.NotEmpty(draft.Agents);

        foreach (AgentDraft agent in draft.Agents)
        {
            IntentDraft intent = draft.Intents.First(i =>
                string.Equals(i.Name, agent.ID, StringComparison.OrdinalIgnoreCase));

            string card = CardProjection.Render(intent, agent);

            Assert.DoesNotContain(intent.Description!, card, StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(agent.Tools, tool => card.Contains(tool.Name!, StringComparison.OrdinalIgnoreCase)
                                                       && !card.Contains($"skill '{tool.Name}'", StringComparison.Ordinal));
        }

        // Two desks answering for the same thing in the same words is one desk as far as a caller can
        // tell, and which of them is asked comes down to the order they were declared in.
        List<string> territories = [.. draft.Agents.Select(agent => AgentRows.Plain(agent.ConsultMeFor)!.Trim())];

        Assert.Equal(territories.Count, territories.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>What validation says about one desk, read on a domain holding only that desk.</summary>
    private IReadOnlyList<ValidationFinding> Findings(IntentDraft intent, AgentDraft agent)
    {
        using IServiceScope scope = fixture.NewScope();

        return scope.ServiceProvider
            .GetRequiredService<IDraftValidationService>()
            .Validate(new DomainDraft { Intents = [intent], Agents = [agent] });
    }
}
