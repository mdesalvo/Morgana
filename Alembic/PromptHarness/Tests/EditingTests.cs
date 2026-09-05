using System.Text;
using System.Text.Json;
using Distiller.Interfaces;
using Distiller.Model;
using Microsoft.Extensions.DependencyInjection;
using PromptHarness.Fixtures;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Correcting an agent that already exists: the other half of what Alembic does and the half every
/// other test in this suite starts too late to see.
/// </summary>
/// <remarks>
/// The rest of the harness interviews a domain into existence. This class begins where a client with
/// a running Morgana begins — an <c>agents.json</c> they already deploy — and asks what happens when
/// they reopen one agent of it to change one section, given that the edit itself walks every section
/// on the way there. Two kinds of thing are asserted and they fail for different reasons.
/// <para>
/// What the model does: the turn that reopens a written section must read it back rather than open
/// as though nothing were there. That is a live defect this suite exists because of — a section of a
/// fully written agent opened twice with the question its first draft would have asked, once
/// offering a cloud of words to an agent that already had a voice.
/// </para>
/// <para>
/// What the machine does: an agent leaves the domain while it is corrected, so putting it back is
/// not bookkeeping but the whole of whether a client's configuration survives being edited — its
/// place in the list, its provenance, its C# facts and every other agent beside it.
/// </para>
/// </remarks>
[Collection(ExamplesDomainCollection.Name)]
[Trait("Stage", "Editing")]
public sealed class EditingTests
{
    private readonly ExamplesDomainFixture corrected;

    public EditingTests(ExamplesDomainFixture corrected) => this.corrected = corrected;

    private Judge Judge => corrected.Services.GetRequiredService<Judge>();

    // ---------------------------------------------------------------- what the model does

    [Fact]
    public async Task Reopening_a_written_section_speaks_about_the_voice_that_exists()
    {
        Assert.False(string.IsNullOrWhiteSpace(corrected.Opening),
            $"Reopening the section asked nothing at all.\n{corrected.Transcript}");

        JudgeVerdict verdict = await Judge.EvaluateTurnAsync(
            "This turn is addressed to someone whose agent ALREADY HAS a personality: it says "
            + "something specific about how that agent currently sounds and asks what should change "
            + "about it. It is NOT the question you would ask if no personality had been written yet "
            + "— it does not ask the reader to describe from scratch how the agent should sound and "
            + "it does not present a set of adjectives for them to choose a voice out of as though "
            + "none had been chosen.",
            corrected.Opening,
            TestContext.Current.CancellationToken);

        Assert.True(verdict.Holds,
            $"The reopened step asked as though nothing were written: {verdict.Reason}\n\n{corrected.Transcript}");
    }

    [Fact]
    public void The_correction_actually_changed_the_section_it_was_opened_on()
    {
        Assert.False(string.Equals(corrected.Before.Personality, corrected.After.Personality, StringComparison.Ordinal),
            $"The Personality came back identical to the one that was imported.\n{corrected.Transcript}");
    }

    [Fact]
    public void The_correction_left_every_other_section_of_that_agent_alone()
    {
        // The edit walks Target, Toolkit, Instructions and Formatting on the way to Personality and
        // after it — that is the whole point, so each gets the chance to notice what the change
        // moved — but none of them had anything to notice here and a pass with nothing to change
        // does not call its own Set tool at all. So this is not merely "correcting does not widen
        // beyond one section" any more: it is a live proof that walking four sections which needed no
        // change actually left them untouched rather than quietly rewritten in passing.
        Assert.Equal(corrected.Before.Target, corrected.After.Target);
        Assert.Equal(corrected.Before.Instructions, corrected.After.Instructions);
        Assert.Equal(corrected.Before.Formatting, corrected.After.Formatting);
    }

    [Fact]
    public void The_marker_the_framework_fences_a_section_with_comes_back_on_it()
    {
        // The labels are Morgana's own lexicon, guaranteed in code rather than asked of a model. A
        // corrected section is written by the same tool that writes a fresh one, so it is fenced the
        // same way — and a domain layer arriving unlabelled would leave half the composed prompt
        // without the markers the other half has.
        Assert.StartsWith("[PERSONALITY]", corrected.After.Personality?.TrimStart() ?? string.Empty,
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- what the machine does

    [Fact]
    public void The_corrected_agent_goes_back_where_it_came_from()
    {
        DomainDraft draft = Current;

        // Not "is present": present at the end of the list is exactly the failure. The order of a
        // domain is the client's — it is what they read on Import and on Review — and an agent that
        // walks to the bottom every time somebody fixes a sentence turns that order into a history
        // of edits.
        Assert.Equal(corrected.AgentAt, draft.Agents.FindIndex(a =>
            string.Equals(a.ID, ExamplesDomainFixture.Edited, StringComparison.OrdinalIgnoreCase)));

        Assert.Equal(corrected.IntentAt, draft.Intents.FindIndex(i =>
            string.Equals(i.Name, ExamplesDomainFixture.Edited, StringComparison.OrdinalIgnoreCase)));

        // And exactly once. An agent under correction is out of the domain precisely so that letting
        // it back in cannot write a second copy of it.
        Assert.Single(draft.Agents.Where(a =>
            string.Equals(a.ID, ExamplesDomainFixture.Edited, StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void An_imported_agent_that_was_changed_comes_back_Revised()
    {
        // Imported means read from the uploaded configuration and not touched since and this one
        // has been. Saying so is the migration report's entire job: a client who brought ten agents
        // has to be told which of them this sitting changed and Authored would be a claim that it
        // exists in no file they own.
        Assert.Equal(Provenance.Imported, corrected.Before.Origin);
        Assert.Equal(Provenance.Revised, corrected.After.Origin);
    }

    [Fact]
    public async Task The_C_sharp_facts_the_agent_arrived_with_are_not_rewritten()
    {
        AgentCodeFacts facts = corrected.After.Code;

        // A fresh agent gets its class names proposed from the naming convention and the record
        // flagged Inferred. A corrected one keeps what it arrived with, which may not be a guess at
        // all — a save file and an archive both carry the real namespace, the real class names and
        // the tier — and writing an inference over a fact, then flagging the result Inferred, would
        // lose something Alembic cannot get back and announce the loss as an improvement.
        DomainDraft fresh = await Imported();
        AgentCodeFacts asImported = fresh.Agents
            .Single(a => string.Equals(a.ID, ExamplesDomainFixture.Edited, StringComparison.OrdinalIgnoreCase))
            .Code;

        Assert.Equal(asImported.Namespace, facts.Namespace);
        Assert.Equal(asImported.AgentClassName, facts.AgentClassName);
        Assert.Equal(asImported.ToolClassName, facts.ToolClassName);
        Assert.Equal(asImported.Tier, facts.Tier);
    }

    [Fact]
    public void Every_agent_the_client_did_not_open_comes_back_untouched()
    {
        // The round-trip invariant, held across an edit rather than across an import: a client who
        // opens one agent of four to fix a sentence gets the other three back exactly as they
        // brought them and Alembic does not need to understand them to promise it.
        IDraftExportService export = corrected.Services.GetRequiredService<IDraftExportService>();

        Dictionary<string, JsonElement> before = AgentsById(corrected.ExportedBefore);
        Dictionary<string, JsonElement> after = AgentsById(export.Export(Current));

        Assert.Equal(before.Keys.OrderBy(k => k), after.Keys.OrderBy(k => k));

        foreach (string id in before.Keys.Where(k =>
                     !string.Equals(k, ExamplesDomainFixture.Edited, StringComparison.OrdinalIgnoreCase)))
        {
            Assert.Equal(before[id].GetRawText(), after[id].GetRawText());
        }
    }

    // ---------------------------------------------------------------- helpers

    private DomainDraft Current =>
        corrected.Services.GetRequiredService<IDraftStateService>().Current
        ?? throw new InvalidOperationException("The correction left no domain behind.");

    /// <summary>The Examples domain as it arrives, for comparing against what an edit gave back.</summary>
    // Awaited rather than blocked on: blocking a test thread on an async call deadlocks against
    // xunit's synchronization context and it does so only once the assembly runs as a whole, which
    // is the worst way for it to be found.
    private async Task<DomainDraft> Imported()
    {
        IDraftImportService import = corrected.Services.GetRequiredService<IDraftImportService>();

        using MemoryStream bytes = new MemoryStream(corrected.ExportedBefore);

        DraftImportResult result = await import.ImportAsync(bytes, "examples-agents.json");

        return result.Draft ?? throw new InvalidOperationException("The exported domain no longer imports.");
    }

    /// <summary>The agents of an exported configuration, by ID, as raw JSON to compare verbatim.</summary>
    private static Dictionary<string, JsonElement> AgentsById(byte[] agentsJson)
    {
        using JsonDocument document = JsonDocument.Parse(Encoding.UTF8.GetString(agentsJson));

        return document.RootElement.GetProperty("Agents")
            .EnumerateArray()
            .ToDictionary(
                agent => agent.GetProperty("ID").GetString() ?? string.Empty,
                agent => agent.Clone());
    }
}
