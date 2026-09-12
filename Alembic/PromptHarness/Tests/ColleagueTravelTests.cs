using System.Text;
using Distiller.Interfaces;
using Distiller.Model;
using Distiller.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// Where a colleague and the section written for one have to survive: the emitted C#, the save
/// file, the exported configuration and the report that tells a client what changed.
/// </summary>
/// <remarks>
/// No model is called and none would help — every assertion here is decidable by reading what came
/// out. What makes the class worth its place is that a colleague travels differently from
/// everything else Alembic writes, and each half is silent when it goes wrong.
/// <para>
/// <c>ConsultMeFor</c> is prose and goes out in <c>agents.json</c> like the other four sections. An
/// edge does not: <c>agents.json</c> has no room for it, so it exists in the emitted C# and in
/// Alembic's own save file and nowhere else. A domain that lost its edges on the way through a save
/// file would come back looking complete, with every desk silently unable to ask the one next to it.
/// </para>
/// </remarks>
[Trait("Stage", "Rules")]
public sealed class ColleagueTravelTests
{
    // ---- The emitted C# -------------------------------------------------------------------------

    [Fact]
    public void A_colleague_of_this_domain_is_declared_by_intent_alone()
    {
        string generated = AgentSource(Agent("reservations", new Morgana.AI.Records.PeerReference("events")));

        Assert.Contains("[ConsultsAgent(\"events\")]", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void A_colleague_at_a_partner_is_declared_with_the_instance_that_publishes_it()
    {
        // The instance is a name and not an address: whose desk to call is the agent author's
        // decision, where that desk runs is the deployment's — so the name has to reach the C#
        // exactly as it was written, and startup matches it against the partner of that name.
        string generated = AgentSource(Agent("reservations", new Morgana.AI.Records.PeerReference("shipping", "acme")));

        Assert.Contains("[ConsultsAgent(\"shipping\", \"acme\")]", generated, StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_nobody_consults_declares_nothing()
    {
        // Consultation is opt-in and pays for itself only where it is declared: an agent that names
        // no colleague must come out of the emit exactly as it did before any of this existed.
        string generated = AgentSource(Agent("reservations"));

        Assert.DoesNotContain("ConsultsAgent", generated, StringComparison.Ordinal);
    }

    // ---- The save file ---------------------------------------------------------------------------

    [Fact]
    public async Task The_save_file_brings_every_colleague_back()
    {
        DomainDraft draft = Domain(
            Agent("reservations",
                new Morgana.AI.Records.PeerReference("events"),
                new Morgana.AI.Records.PeerReference("shipping", "acme")),
            Agent("events"));

        DomainDraft resumed = await SavedAndReadBackAsync(draft);

        AgentDraft reservations = resumed.Agents.Single(a => a.ID == "reservations");

        Assert.Collection(reservations.Code.Consults,
            local =>
            {
                Assert.Equal("events", local.Intent);
                Assert.Null(local.Instance);
            },
            remote =>
            {
                Assert.Equal("shipping", remote.Intent);
                Assert.Equal("acme", remote.Instance);
            });

        // The two are told apart by whether an instance is named, so a local colleague coming back
        // with an empty name instead of none would be a different colleague: startup would look for
        // a partner rather than for an agent of this domain.
        Assert.Equal("Whatever falls to reservations.", reservations.ConsultMeFor);
    }

    // ---- The exported configuration ---------------------------------------------------------------

    [Fact]
    public async Task The_statement_a_colleague_reads_survives_the_round_trip()
    {
        // The fifth section is a top-level member of the prompt rather than one more key beside the
        // others, so nothing generic would have carried it: a client's own statement of what their
        // desk answers for would have been dropped on export without a sound.
        DomainDraft draft = Domain(Agent("reservations"), Agent("events"));
        draft.Agents[0].ConsultMeFor = "Table bookings in the dining room, and what is free when.";

        DomainDraft reimported = await ExportedAndImportedAsync(draft);

        Assert.Equal(
            "Table bookings in the dining room, and what is free when.",
            reimported.Agents.Single(a => a.ID == "reservations").ConsultMeFor);
    }

    // ---- The migration report -----------------------------------------------------------------------

    [Fact]
    public void A_colleague_at_a_partner_is_reported_with_what_the_deployment_still_owes_it()
    {
        // The archive carries neither the address nor the shared key, and a partner missing from the
        // configuration is startup-fatal rather than a colleague that quietly never appears — so the
        // one place that can say so is the report the client reads before deploying.
        DomainDraft draft = Domain(Agent("reservations", new Morgana.AI.Records.PeerReference("shipping", "acme")));

        MigrationReport report = new MigrationReportService().Build(draft);

        Assert.Contains(report.Entries, e =>
            e.Detail.Contains("Morgana:AgentToAgent:Partners", StringComparison.Ordinal)
            && e.Detail.Contains("acme", StringComparison.Ordinal));
    }

    // ---- What these read --------------------------------------------------------------------------

    /// <summary>The generated half of one agent's class, as the archive would carry it.</summary>
    private static string AgentSource(AgentDraft agent) =>
        new CodeEmitService()
            .Emit(agent, agent.ID!)
            .Single(file => file.Path.EndsWith(".g.cs", StringComparison.Ordinal) && file.Path.StartsWith("Agents/", StringComparison.Ordinal))
            .Content;

    /// <summary>The domain as it comes back out of Alembic's own save file.</summary>
    private static async Task<DomainDraft> SavedAndReadBackAsync(DomainDraft draft)
    {
        DraftSerializationService drafts = new(NullLogger.Instance);
        using MemoryStream saved = new MemoryStream(drafts.Serialize(draft));

        return await drafts.DeserializeAsync(saved, TestContext.Current.CancellationToken)
               ?? throw new InvalidOperationException("The domain did not survive its own save file.");
    }

    /// <summary>The domain as it comes back out of an exported <c>agents.json</c>.</summary>
    private static async Task<DomainDraft> ExportedAndImportedAsync(DomainDraft draft)
    {
        byte[] exported = new DraftExportService().Export(draft);
        using MemoryStream configuration = new MemoryStream(exported);

        DraftImportResult imported = await new DraftImportService(NullLogger.Instance)
            .ImportAsync(configuration, "agents.json", TestContext.Current.CancellationToken);

        return imported.Draft
               ?? throw new InvalidOperationException($"The exported configuration did not import: {imported.Error}\n"
                                                      + Encoding.UTF8.GetString(exported));
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
