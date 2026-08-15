using Alembic.Model;
using AlembicHarness.Fixtures;
using AlembicHarness.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AlembicHarness.Tests;

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
/// reflects the doctrine, and either way is worth seeing named, not folded into a single broad
/// "does this look right" question a judge could pass by being right about only one section.
/// </remarks>
public sealed class DoctrineTests : IClassFixture<BistroLunaInterviewFixture>
{
    private readonly BistroLunaInterviewFixture interviewed;
    private AgentDraft Agent => interviewed.Draft.Agents[0];

    public DoctrineTests(BistroLunaInterviewFixture interviewed) => this.interviewed = interviewed;

    private Judge Judge => interviewed.Services.GetRequiredService<Judge>();

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

    [Fact]
    public Task Target_never_reads_as_a_generic_assistant() => AssertDoesNotHoldAsync(
        "This text describes the agent in generic terms — a virtual assistant, a chatbot, a helpful "
        + "bot, or neutral customer service staff — rather than as a specific persona belonging to Morgana.",
        Agent.Target ?? string.Empty,
        "Target reads as a generic assistant rather than a persona of Morgana");

    [Fact]
    public Task Target_states_an_explicit_boundary() => AssertHoldsAsync(
        "This text explicitly states at least one thing the agent must never do or is not able to "
        + "do, in addition to describing what it is for.",
        Agent.Target ?? string.Empty,
        "Target states no explicit boundary");

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
