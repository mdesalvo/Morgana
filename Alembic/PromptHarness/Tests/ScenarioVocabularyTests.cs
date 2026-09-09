using Distiller.Harness;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The one rule that keeps a derived scenario an assertion rather than the appearance of one: it
/// may drop a key of the template it came from and may never add one.
/// </summary>
/// <remarks>
/// This is checked at the emit because it cannot be caught anywhere later. The harness's own loader
/// ignores a key it does not recognise, so an invented one is dropped without a sound: the scenario
/// loads, runs, passes and asserts nothing at all. A client reading a green suite has no way to
/// tell that from coverage.
/// <para>
/// Free of any model, which is what lets it run on every change rather than on the runs somebody
/// pays for. What a model supplies is the domain's own words; whether the result is still a
/// scenario is arithmetic over the template's own keys.
/// </para>
/// </remarks>
[Trait("Stage", "Rules")]
public sealed class ScenarioVocabularyTests
{
    /// <summary>A derivation as a model would return it, using only keys the templates use.</summary>
    private const string Derived =
        """
        id: "reservations-boundary-refusal"
        description: >-
          A caller asks for the back room, which this desk does not let out.
        runs: 3
        minPasses: 3
        turns:
          - say: "Can you put the back room aside for my daughter's birthday?"
            expect:
              agent: "ReservationsAgent"
              textNotEmpty: true
            judge:
              - "the reply declines to book the back room and says what it can do instead"
        """;

    [Fact]
    public void A_derivation_that_uses_only_the_templates_own_keys_is_accepted()
    {
        DerivedScenario derivation = ScenarioDerivation.Check(
            ScenarioTemplateLibrary.Vocabulary, "reservations-boundary-refusal", Derived);

        Assert.Null(derivation.Problem);
        Assert.Null(derivation.NotApplicable);
        Assert.NotNull(derivation.Content);
    }

    [Fact]
    public void A_derivation_that_invents_a_key_ships_with_the_problem_written_on_it()
    {
        // The characteristic shape of it: a plausible key nothing binds, reached for because it
        // reads like something a test framework would have. It ships anyway — a silently missing
        // scenario costs the client more than a visibly broken one — with the problem named.
        string invented = Derived.Replace(
            "      textNotEmpty: true",
            "      textNotEmpty: true\n      textContains: \"back room\"",
            StringComparison.Ordinal);

        DerivedScenario derivation = ScenarioDerivation.Check(
            ScenarioTemplateLibrary.Vocabulary, "reservations-boundary-refusal", invented);

        Assert.NotNull(derivation.Content);
        Assert.Contains("textContains", derivation.Problem ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void A_derivation_that_drops_a_key_is_a_narrower_scenario_and_not_a_defect()
    {
        // Dropping is legitimate: a domain with nothing to withhold has nothing to assert about
        // withholding, and the assertions that remain are still the domain's own.
        string narrower = Derived[..Derived.IndexOf("    judge:", StringComparison.Ordinal)];

        DerivedScenario derivation = ScenarioDerivation.Check(
            ScenarioTemplateLibrary.Vocabulary, "reservations-boundary-refusal", narrower);

        Assert.Null(derivation.Problem);
        Assert.NotNull(derivation.Content);
    }

    [Fact]
    public void A_derivation_still_carrying_a_placeholder_is_reported()
    {
        // A template with a domain glued to one half of it: it parses, and it asserts against a tool
        // no agent has.
        string half = Derived.Replace("\"ReservationsAgent\"", "\"{{AgentClass}}\"", StringComparison.Ordinal);

        DerivedScenario derivation = ScenarioDerivation.Check(
            ScenarioTemplateLibrary.Vocabulary, "reservations-boundary-refusal", half);

        Assert.Contains("placeholder", derivation.Problem ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Declining_a_use_case_this_domain_has_no_instance_of_is_an_answer()
    {
        // A read-only toolkit has no confirmation to protect, and a scenario written for one would
        // fail a correct agent. The decline is believed and nothing is written.
        DerivedScenario derivation = ScenarioDerivation.Check(
            ScenarioTemplateLibrary.Vocabulary,
            "reservations-confirmation-before-commit",
            "not-applicable: nothing this agent does is irreversible.");

        Assert.Null(derivation.Content);
        Assert.Equal("nothing this agent does is irreversible.", derivation.NotApplicable);
    }

    [Fact]
    public void The_vocabulary_is_exactly_what_the_templates_themselves_use()
    {
        // The union is the whole line between an assertion and the appearance of one, and it is read
        // off the templates rather than declared beside them — so a template added to the folder
        // widens it by construction and nothing else can.
        Assert.NotEmpty(ScenarioTemplateLibrary.All);
        Assert.Equal(
            ScenarioTemplateLibrary.All.SelectMany(template => template.Keys).ToHashSet(StringComparer.Ordinal),
            ScenarioTemplateLibrary.Vocabulary);
    }
}
