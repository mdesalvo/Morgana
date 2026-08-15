using System.Text;
using Alembic.Interfaces;
using Alembic.Model;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IMigrationReportService"/>: a Draft against its own baseline, and no model
/// asked.
/// </summary>
/// <remarks>
/// Every question the report answers is decidable by comparing two Drafts. What it deliberately
/// does not attempt is judgement — whether a revised description is <em>better</em>, whether two
/// intents now overlap — because that needs a model, belongs to the coherence pass, and would turn
/// a report the client must trust literally into one they have to second-guess.
/// </remarks>
public class MigrationReportService : IMigrationReportService
{
    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="draft"/> is compared against its own <see cref="DomainDraft.Baseline"/> — the
    /// domain as it stood at import, frozen the moment it arrived — never against the uploaded bytes
    /// directly, so the comparison is Draft-to-Draft and every field means the same thing on both
    /// sides. An empty baseline (nothing was uploaded this sitting) still compares cleanly: every
    /// intent and agent in <paramref name="draft"/> reads as newly added.
    /// </remarks>
    public MigrationReport Build(DomainDraft draft)
    {
        DomainDraft baseline = draft.Baseline ?? new DomainDraft();
        List<MigrationEntry> entries = [];

        CompareIntents(draft, baseline, entries);
        CompareAgents(draft, baseline, entries);

        // Sorted by what it costs to act on, not by where it sits in the file: a removed tool and a
        // changed signature are work, a revised sentence is a re-read.
        List<MigrationEntry> ordered = [.. entries.OrderBy(e => e.Change).ThenBy(e => e.Where, StringComparer.Ordinal)];

        return new MigrationReport(draft.Baseline?.ImportedFrom, ordered, Render(draft, ordered));
    }

    /// <summary>
    /// Intents added, removed, or reworded.
    /// </summary>
    /// <param name="draft">The domain as it stands now.</param>
    /// <param name="baseline">The domain as it stood at import.</param>
    /// <param name="entries">The report under construction — findings are appended, never returned.</param>
    private static void CompareIntents(DomainDraft draft, DomainDraft baseline, List<MigrationEntry> entries)
    {
        // The fallback intent is not on the map and no interview ever authors it — DomainDraft
        // ensures it exists in every domain, baseline included where the baseline is a real upload,
        // but a fresh baseline (nothing uploaded this sitting, see the remarks on Build) has none of
        // its own yet. Reporting it as "Added" would say the interview did something it never did,
        // to a client who cannot act on it: it needs no plugin registration and no agent answers it.
        foreach (IntentDraft intent in draft.Intents.Where(i =>
                     !string.IsNullOrWhiteSpace(i.Name) && !string.Equals(i.Name, DomainDraft.FallbackIntent, StringComparison.OrdinalIgnoreCase)))
        {
            IntentDraft? was = Find(baseline.Intents, intent.Name);

            if (was is null)
            {
                entries.Add(new MigrationEntry(MigrationKind.Intent, intent.Name!, MigrationChange.Added,
                    "New intent. Its agent class must be registered by the plugin, and the classifier now weighs this description against every other."));
                continue;
            }

            if (!string.Equals(was.Description, intent.Description, StringComparison.Ordinal))
                entries.Add(new MigrationEntry(MigrationKind.Intent, intent.Name!, MigrationChange.Revised,
                    "The classifier's description changed. Nothing to compile; routing behaviour changes from the next conversation."));
        }

        foreach (IntentDraft gone in baseline.Intents.Where(i =>
                     !string.IsNullOrWhiteSpace(i.Name)
                     && !string.Equals(i.Name, DomainDraft.FallbackIntent, StringComparison.OrdinalIgnoreCase)
                     && Find(draft.Intents, i.Name) is null))
        {
            entries.Add(new MigrationEntry(MigrationKind.Intent, gone.Name!, MigrationChange.Removed,
                "Removed. Any [HandlesIntent] agent still declaring it fails startup: HandlesIntentAgentRegistryService checks the pairing in both directions."));
        }
    }

    /// <summary>
    /// Agents added or reworded, and every toolkit inside them.
    /// </summary>
    /// <param name="draft">The domain as it stands now.</param>
    /// <param name="baseline">The domain as it stood at import.</param>
    /// <param name="entries">The report under construction — findings are appended, never returned.</param>
    private static void CompareAgents(DomainDraft draft, DomainDraft baseline, List<MigrationEntry> entries)
    {
        foreach (AgentDraft agent in draft.Agents.Where(a => !string.IsNullOrWhiteSpace(a.ID)))
        {
            AgentDraft? was = Find(baseline.Agents, agent.ID);

            if (was is null)
            {
                entries.Add(new MigrationEntry(MigrationKind.Agent, agent.ID!, MigrationChange.Added,
                    $"New agent. Its generated class and {(agent.Tools.Count > 0 ? "tool class are" : "class is")} in this archive and do not exist in your tree yet."));
            }
            else if (Prose(was) != Prose(agent))
            {
                entries.Add(new MigrationEntry(MigrationKind.Agent, agent.ID!, MigrationChange.Revised,
                    "Prose changed. It lives entirely in agents.json — replace the file and the change is live, with nothing to rebuild."));
            }

            CompareTools(agent, was, entries);
        }

        foreach (AgentDraft gone in baseline.Agents.Where(a =>
                     !string.IsNullOrWhiteSpace(a.ID) && Find(draft.Agents, a.ID) is null))
        {
            entries.Add(new MigrationEntry(MigrationKind.Agent, gone.ID!, MigrationChange.Removed,
                "Removed from the configuration. Delete its agent and tool classes: an agent class with no intent behind it fails startup."));
        }
    }

    /// <summary>
    /// One agent's toolkit, with the signature comparison the compiled half depends on.
    /// </summary>
    /// <param name="agent">The agent as it stands now.</param>
    /// <param name="was">The same agent at baseline, or <c>null</c> if the agent itself is new — in
    /// which case every tool it declares is reported as new too, folded into the agent's own entry
    /// rather than repeated per tool (see the <c>was is not null</c> guard below).</param>
    /// <param name="entries">The report under construction — findings are appended, never returned.</param>
    private static void CompareTools(AgentDraft agent, AgentDraft? was, List<MigrationEntry> entries)
    {
        List<ToolDraft> before = was?.Tools ?? [];

        foreach (ToolDraft tool in agent.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            string where = $"{agent.ID}.{tool.Name}";
            ToolDraft? previous = before.FirstOrDefault(t => string.Equals(t.Name, tool.Name, StringComparison.Ordinal));

            if (previous is null)
            {
                if (was is not null)
                    entries.Add(new MigrationEntry(MigrationKind.Signature, where, MigrationChange.Added,
                        $"New tool. The generated half declares `public partial Task<string> {tool.Name}({Signature(tool)})`, and the file will not compile until you implement it in the half you own."));

                continue;
            }

            if (Signature(previous) != Signature(tool))
                entries.Add(new MigrationEntry(MigrationKind.Signature, where, MigrationChange.SignatureChanged,
                    $"`{Signature(previous)}` became `{Signature(tool)}`. Change your method to match: the generated declaration moves on its own, and MorganaToolAdapter.AddTool refuses the pair at startup if it does not."));
            else if (!string.Equals(previous.Description, tool.Description, StringComparison.Ordinal)
                     || previous.Parameters.Zip(tool.Parameters).Any(p => !string.Equals(p.First.Description, p.Second.Description, StringComparison.Ordinal)))
                entries.Add(new MigrationEntry(MigrationKind.Tool, where, MigrationChange.Revised,
                    "Description changed. It reaches the model through agents.json and the schema, so nothing needs rebuilding."));
        }

        foreach (ToolDraft gone in before.Where(t =>
                     !string.IsNullOrWhiteSpace(t.Name)
                     && !agent.Tools.Any(x => string.Equals(x.Name, t.Name, StringComparison.Ordinal))))
        {
            entries.Add(new MigrationEntry(MigrationKind.Signature, $"{agent.ID}.{gone.Name}", MigrationChange.Removed,
                $"Gone from the configuration. Its generated declaration disappears, so `{gone.Name}` in the half you own becomes an orphan method — delete it or the partial no longer matches."));
        }
    }

    /// <summary>
    /// Renders the report as the <c>MIGRATION.md</c> in the archive.
    /// </summary>
    /// <param name="draft">The domain the report is about — read here only for <see cref="DomainDraft.Baseline"/>,
    /// to say what the comparison was against.</param>
    /// <param name="entries">The findings <see cref="Build"/> collected, already ordered by cost to act on.</param>
    private static string Render(DomainDraft draft, IReadOnlyList<MigrationEntry> entries)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine("# Migration report");
        sb.AppendLine();

        if (draft.Baseline is null)
        {
            sb.AppendLine("No configuration was uploaded into this session, so there is nothing to compare against:");
            sb.AppendLine("everything in this archive is new. Drop it into a plugin project, point Morgana's");
            sb.AppendLine("`Morgana:Plugins:Directories` at the build output, and start.");
            return sb.ToString();
        }

        sb.AppendLine($"Against `{draft.Baseline.ImportedFrom}`, as uploaded.");
        sb.AppendLine();

        if (entries.Count == 0)
        {
            sb.AppendLine("Nothing changed. The `agents.json` in this archive is equivalent to the one you uploaded,");
            sb.AppendLine("and the generated sources match the code you already have.");
            return sb.ToString();
        }

        sb.AppendLine("Alembic never sees your tree, so nothing here is applied for you. What follows is every");
        sb.AppendLine("difference, ordered by what it costs to act on.");
        sb.AppendLine();

        foreach (IGrouping<MigrationChange, MigrationEntry> group in entries.GroupBy(e => e.Change).OrderBy(g => g.Key))
        {
            sb.AppendLine($"## {Heading(group.Key)}");
            sb.AppendLine();

            foreach (MigrationEntry entry in group)
                sb.AppendLine($"- **`{entry.Where}`** ({entry.Kind.ToString().ToLowerInvariant()}) — {entry.Detail}");

            sb.AppendLine();
        }

        sb.AppendLine("## The two halves");
        sb.AppendLine();
        sb.AppendLine("Every `*.g.cs` in this archive is Alembic's and is regenerated in full — overwrite yours.");
        sb.AppendLine("Every matching `*.cs` is yours: Alembic wrote it once as a working mock and will not write it");
        sb.AppendLine("again. Where a signature above changed, that is the file to edit.");

        return sb.ToString();
    }

    /// <summary>
    /// The heading a change group gets, in the client's terms rather than the enum's.
    /// </summary>
    private static string Heading(MigrationChange change) => change switch
    {
        MigrationChange.Removed => "Removed — delete the code behind these",
        MigrationChange.SignatureChanged => "Signatures changed — edit the half you own",
        MigrationChange.Added => "New — this code does not exist in your tree yet",
        _ => "Reworded — configuration only, nothing to rebuild"
    };

    /// <summary>
    /// A tool's parameter list, exactly as the generated declaration will render it.
    /// </summary>
    /// <remarks>
    /// Kept independent of <see cref="CodeEmitService"/>'s own signature rendering rather than
    /// sharing it: this one exists purely to compare two Drafts as strings, and coupling it to the
    /// emitter would make an unrelated formatting change there silently start firing (or silently
    /// stop firing) signature-changed findings here.
    /// </remarks>
    private static string Signature(ToolDraft tool) =>
        string.Join(", ", tool.Parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p.Required ? $"string {p.Name}" : $"string? {p.Name} = null"));

    /// <summary>
    /// An agent's four sections joined, for a single comparison.
    /// </summary>
    private static string Prose(AgentDraft agent) =>
        string.Join("", agent.Target, agent.Instructions, agent.Personality, agent.Formatting);

    /// <summary>
    /// Finds an intent by name, case-insensitively — the same lookup the classifier is indifferent
    /// to casing for.
    /// </summary>
    private static IntentDraft? Find(List<IntentDraft> intents, string? name) =>
        intents.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds an agent by its intent id, case-insensitively.
    /// </summary>
    private static AgentDraft? Find(List<AgentDraft> agents, string? id) =>
        agents.FirstOrDefault(a => string.Equals(a.ID, id, StringComparison.OrdinalIgnoreCase));
}
