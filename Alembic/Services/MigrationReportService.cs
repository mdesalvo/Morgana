using System.Globalization;
using System.Text;
using Alembic.Interfaces;
using Alembic.Models;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IMigrationReportService"/>: a Draft against its own baseline, with no model asked.
/// It never judges whether a revision is better or two intents now overlap, which is the coherence pass's: a report the client acts on literally
/// must not need second-guessing.
/// </summary>
public class MigrationReportService : IMigrationReportService
{
    /// <summary>
    /// Diffs the Draft against its own <see cref="DomainDraft.Baseline"/>, the domain frozen at import, so every field means the same on both sides.
    /// Without a baseline, as in a domain born here, every intent and agent reads as added.
    /// </summary>
    public MigrationReport Build(DomainDraft draft)
    {
        // A domain born in Alembic compares against an empty one.
        DomainDraft baseline = draft.Baseline ?? new DomainDraft();
        List<MigrationEntry> entries = [];

        // Intents first, then agents with their toolkits, each comparison appending what it finds.
        CompareIntents(draft, baseline, entries);
        CompareAgents(draft, baseline, entries);

        // Sorted by what it costs to act on, not by where it sits in the file: a removed tool and a
        // changed signature are work, a revised sentence is a re-read.
        List<MigrationEntry> ordered = [.. entries.OrderBy(entry => entry.Change).ThenBy(entry => entry.Where, StringComparer.Ordinal)];

        // The entries for the page and the same report rendered as MIGRATION.md for the archive.
        return new MigrationReport(draft.Baseline?.ImportedFrom, ordered, Render(draft, ordered));
    }

    /// <summary>Appends the intents added, removed or reworded.</summary>
    /// <param name="draft">The domain as it stands now.</param>
    /// <param name="baseline">The domain as it stood at import.</param>
    /// <param name="entries">The report under construction, which receives each finding.</param>
    private static void CompareIntents(DomainDraft draft, DomainDraft baseline, List<MigrationEntry> entries)
    {
        // The fallback intent is on neither side of a comparison worth reporting: no interview authors
        // it and nothing emitted declares it, while an uploaded configuration may still carry one, which
        // the importer already removed and told the client about. Reporting it again as removed would
        // send them looking for an agent to unregister for an intent no agent ever answered.
        foreach (IntentDraft intent in draft.Intents.Where(intentDraft =>
                     !string.IsNullOrWhiteSpace(intentDraft.Name) && !string.Equals(intentDraft.Name, DomainDraft.FallbackIntent, StringComparison.OrdinalIgnoreCase)))
        {
            // The same intent in the uploaded file, matched case-insensitively as the classifier matches it.
            IntentDraft? was = Find(baseline.Intents, intent.Name);

            // Not in the upload: the plugin has to register an agent for it.
            if (was is null)
            {
                entries.Add(new MigrationEntry(MigrationKind.Intent, intent.Name!, MigrationChange.Added,
                    "New intent. Its agent class must be registered by the plugin and the classifier now weighs this description against every other."));
                continue;
            }

            // A reworded description changes routing and nothing that compiles.
            if (!string.Equals(was.Description, intent.Description, StringComparison.Ordinal))
                entries.Add(new MigrationEntry(MigrationKind.Intent, intent.Name!, MigrationChange.Revised,
                    "The classifier's description changed. Nothing to compile; routing behaviour changes from the next conversation."));
        }

        // An uploaded intent the Draft no longer holds leaves an agent class whose intent is gone, which fails startup.
        entries.AddRange(
            baseline.Intents.Where(intentDraft => !string.IsNullOrWhiteSpace(intentDraft.Name)
                                         && !string.Equals(intentDraft.Name, DomainDraft.FallbackIntent, StringComparison.OrdinalIgnoreCase)
                                         && Find(draft.Intents, intentDraft.Name) is null)
                            .Select(gone => new MigrationEntry(MigrationKind.Intent, gone.Name!, MigrationChange.Removed, "Removed. Any [HandlesIntent] agent still declaring it fails startup: HandlesIntentAgentRegistryService checks the pairing in both directions.")));
    }

    /// <summary>Appends the agents added, reworded or removed and every change inside their toolkits.</summary>
    /// <param name="draft">The domain as it stands now.</param>
    /// <param name="baseline">The domain as it stood at import.</param>
    /// <param name="entries">The report under construction, which receives each finding.</param>
    private static void CompareAgents(DomainDraft draft, DomainDraft baseline, List<MigrationEntry> entries)
    {
        // Every named agent of the Draft, against its counterpart in the upload.
        foreach (AgentDraft agent in draft.Agents.Where(agentDraft => !string.IsNullOrWhiteSpace(agentDraft.ID)))
        {
            AgentDraft? was = Find(baseline.Agents, agent.ID);

            // Not in the upload: its classes exist in the archive and nowhere in the client's tree.
            if (was is null)
            {
                entries.Add(new MigrationEntry(MigrationKind.Agent, agent.ID!, MigrationChange.Added,
                    $"New agent. Its generated class and {(agent.Tools.Count > 0 ? "tool class are" : "class is")} in this archive and do not exist in your tree yet."));
            }
            // Reworded: the change lives in agents.json alone.
            else if (Prose(was) != Prose(agent))
            {
                entries.Add(new MigrationEntry(MigrationKind.Agent, agent.ID!, MigrationChange.Revised,
                    "Prose changed. It lives entirely in agents.json — replace the file and the change is live, with nothing to rebuild."));
            }

            // Separately from the prose, because it lands somewhere else: [ConsultsAgent] is on the
            // agent class, so a gained colleague means re-emitting a file, where a reworded sentence
            // means replacing agents.json and nothing more. An imported baseline carries none of
            // these, the attribute living in C# the upload never had, so only what this sitting
            // declared can show up here.
            if (was is not null && !Same(was.Code.Consults, agent.Code.Consults))
                entries.Add(new MigrationEntry(MigrationKind.Agent, agent.ID!, MigrationChange.Revised,
                    $"Colleagues changed to {PeerNaming.Describe(agent.Code.Consults)}. "
                    + "[ConsultsAgent] lives on the agent class: re-emit it and check the intent it names is one your plugin registers."));

            // Unconditional and unlike everything else here it is not about what changed: a colleague
            // at another installation needs an address and a key this archive deliberately does not carry,
            // a URL being the deployment's word and a shared secret having no business in a download.
            // Said on every report because a partner entry never added (or added under a differently
            // spelled name) is a startup failure wearing a plausible configuration.
            entries.AddRange(
                agent.Code.Consults.Where(colleague => colleague.Instance is not null)
                                   .Select(colleague => new MigrationEntry(MigrationKind.Agent, agent.ID!, MigrationChange.Revised, $"Consults '{colleague.Intent}' at system '{colleague.Instance}'. Declare that system under " + $"Morgana:AgentToAgent:Partners with its Url, the key you share with it and \"OutboundPolicy\": {{ \"Enabled\": true }}, under exactly the name " + $"'{colleague.Instance}' — spelling and spacing included, or startup refuses the agent. Neither the " + "address nor the key is in this archive. That the system really publishes an agent for " + $"'{colleague.Intent}' is its own card's word, read on the first consultation: a mistake there is a " + "warning at run time and the colleague quietly missing, not a startup error.")));

            // The toolkit, whose signatures are what the client's own half compiles against.
            CompareTools(agent, was, entries);
        }

        // An uploaded agent the Draft no longer holds leaves classes behind that fail startup.
        entries.AddRange(
            baseline.Agents.Where(agentDraft => !string.IsNullOrWhiteSpace(agentDraft.ID)
                                         && Find(draft.Agents, agentDraft.ID) is null)
                           .Select(gone => new MigrationEntry(MigrationKind.Agent, gone.ID!, MigrationChange.Removed, "Removed from the configuration. Delete its agent and tool classes: an agent class with no intent behind it fails startup.")));
    }

    /// <summary>Whether two colleague lists say the same thing, order and casing aside, since the order of the attributes changes nothing.</summary>
    private static bool Same(IEnumerable<string> was, IEnumerable<string> now) =>
        new HashSet<string>(was, StringComparer.OrdinalIgnoreCase)
            .SetEquals(new HashSet<string>(now, StringComparer.OrdinalIgnoreCase));

    /// <summary>Whether two colleague sets name the same colleagues, partners included.</summary>
    /// <param name="was">The baseline's colleagues.</param>
    /// <param name="now">The draft's colleagues.</param>
    private static bool Same(
        IEnumerable<Morgana.AI.Records.PeerReference> was,
        IEnumerable<Morgana.AI.Records.PeerReference> now) =>
        Same(was.Select(PeerNaming.Describe), now.Select(PeerNaming.Describe));

    /// <summary>Appends one agent's toolkit changes, with the signature comparison the client's compiled half depends on.</summary>
    /// <param name="agent">The agent as it stands now.</param>
    /// <param name="was">The same agent at baseline; <c>null</c> for a new agent, whose tools its own entry already covers.</param>
    /// <param name="entries">The report under construction, which receives each finding.</param>
    private static void CompareTools(AgentDraft agent, AgentDraft? was, List<MigrationEntry> entries)
    {
        // A new agent compares against no tools at all.
        List<ToolDraft> before = was?.Tools ?? [];

        // Every named tool, matched by its exact name, which is the method name in both halves.
        foreach (ToolDraft tool in agent.Tools.Where(toolDraft => !string.IsNullOrWhiteSpace(toolDraft.Name)))
        {
            string where = $"{agent.ID}.{tool.Name}";
            ToolDraft? previous = before.FirstOrDefault(toolDraft => string.Equals(toolDraft.Name, tool.Name, StringComparison.Ordinal));

            // A tool the upload lacked is a method the client must implement.
            if (previous is null)
            {
                // Under an existing agent only: a new agent's tools ride on its own entry.
                if (was is not null)
                    entries.Add(new MigrationEntry(MigrationKind.Signature, where, MigrationChange.Added,
                        $"New tool. The generated half declares `public partial Task<string> {tool.Name}({Signature(tool)})` and the file will not compile until you implement it in the half you own."));

                continue;
            }

            // A changed parameter list stops the client's half compiling until it matches.
            if (Signature(previous) != Signature(tool))
                entries.Add(new MigrationEntry(MigrationKind.Signature, where, MigrationChange.SignatureChanged,
                    $"`{Signature(previous)}` became `{Signature(tool)}`. Change your method to match: the generated declaration moves on its own and the partial does not compile until your half matches it."));
            // The description and every parameter's scope sit on the generated declaration, so a change
            // to either is a replacement of the generated half with nothing to rewrite by hand.
            else if (!string.Equals(previous.Description, tool.Description, StringComparison.Ordinal)
                     || previous.Parameters.Zip(tool.Parameters).Any(pair =>
                         !string.Equals(pair.First.Description, pair.Second.Description, StringComparison.Ordinal)
                         || !string.Equals(pair.First.Scope ?? string.Empty, pair.Second.Scope ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                         || pair.First.Shared != pair.Second.Shared))
                entries.Add(new MigrationEntry(MigrationKind.Tool, where, MigrationChange.Revised,
                    "Description or scope changed. Both live on the generated declaration, so replace the generated half and rebuild."));
        }

        // A tool gone from the configuration leaves an orphan method in the client's half.
        entries.AddRange(
            before.Where(toolDraft => !string.IsNullOrWhiteSpace(toolDraft.Name)
                                && !agent.Tools.Any(currentTool => string.Equals(currentTool.Name, toolDraft.Name, StringComparison.Ordinal)))
                  .Select(gone => new MigrationEntry(MigrationKind.Signature, $"{agent.ID}.{gone.Name}", MigrationChange.Removed, $"Gone from the configuration. Its generated declaration disappears, so `{gone.Name}` in the half you own becomes an orphan method — delete it or the partial no longer matches.")));
    }

    /// <summary>Renders the report as the <c>MIGRATION.md</c> in the archive.</summary>
    /// <param name="draft">The domain the report is about, read only for its baseline's file name.</param>
    /// <param name="entries">The findings, already ordered by what they cost to act on.</param>
    private static string Render(DomainDraft draft, IReadOnlyList<MigrationEntry> entries)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine("# Migration report");
        sb.AppendLine();

        // A domain born here has nothing to migrate: the report says how to start instead.
        if (draft.Baseline is null)
        {
            sb.AppendLine("No configuration was uploaded into this session, so there is nothing to compare against:");
            sb.AppendLine("everything in this archive is new. Drop it into a plugin project, point Morgana's");
            sb.AppendLine("`Morgana:Plugins:Directories` at the build output and start.");
            return sb.ToString();
        }

        // The upload the comparison was made against, by the name the client gave it.
        sb.AppendLine(CultureInfo.InvariantCulture, $"Against `{draft.Baseline.ImportedFrom}`, as uploaded.");
        sb.AppendLine();

        // An unchanged domain says so, which is itself the answer the client came for.
        if (entries.Count == 0)
        {
            sb.AppendLine("Nothing changed. The `agents.json` in this archive is equivalent to the one you uploaded,");
            sb.AppendLine("and the generated sources match the code you already have.");
            return sb.ToString();
        }

        sb.AppendLine("Alembic never sees your tree, so nothing here is applied for you. What follows is every");
        sb.AppendLine("difference, ordered by what it costs to act on.");
        sb.AppendLine();

        // One section per kind of change, the costliest first.
        foreach (IGrouping<MigrationChange, MigrationEntry> group in entries.GroupBy(entry => entry.Change).OrderBy(changeGroup => changeGroup.Key))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {Heading(group.Key)}");
            sb.AppendLine();

            foreach (MigrationEntry entry in group)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **`{entry.Where}`** ({entry.Kind.ToString().ToLowerInvariant()}) — {entry.Detail}");

            sb.AppendLine();
        }

        // The convention again, beside the changes it tells the client how to apply.
        sb.AppendLine("## The two halves");
        sb.AppendLine();
        sb.AppendLine("Every `*.g.cs` in this archive is Alembic's and is regenerated in full — overwrite yours.");
        sb.AppendLine("Every matching `*.cs` is yours: Alembic wrote it once as a working mock and will not write it");
        sb.AppendLine("again. Where a signature above changed, that is the file to edit.");

        return sb.ToString();
    }

    /// <summary>The heading a change group gets, in the client's terms rather than the enum's.</summary>
    private static string Heading(MigrationChange change) => change switch
    {
        // Each kind says what the client does about it; a revision is the one with nothing to edit.
        MigrationChange.Removed => "Removed — delete the code behind these",
        MigrationChange.SignatureChanged => "Signatures changed — edit the half you own",
        MigrationChange.Added => "New — this code does not exist in your tree yet",
        _ => "Reworded — nothing to edit by hand"
    };

    /// <summary>
    /// A tool's parameter list as the generated declaration renders it, kept apart from <see cref="CodeEmitService"/>'s own rendering.
    /// It only compares two Drafts as strings, so a formatting change in the emitter must not start or stop signature findings here.
    /// </summary>
    private static string Signature(ToolDraft tool) =>
        string.Join(", ", tool.Parameters
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name))
            .Select(parameter => parameter.Required ? $"string {parameter.Name}" : $"string? {parameter.Name} = null"));

    /// <summary>An agent's four sections joined, for a single comparison.</summary>
    private static string Prose(AgentDraft agent) =>
        string.Join("", agent.Target, agent.Instructions, agent.Personality, agent.Formatting);

    /// <summary>Finds an intent by name case-insensitively, as the classifier is indifferent to casing.</summary>
    private static IntentDraft? Find(List<IntentDraft> intents, string? name) =>
        intents.FirstOrDefault(intent => string.Equals(intent.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds an agent by its intent id, case-insensitively.</summary>
    private static AgentDraft? Find(List<AgentDraft> agents, string? id) =>
        agents.FirstOrDefault(agent => string.Equals(agent.ID, id, StringComparison.OrdinalIgnoreCase));
}