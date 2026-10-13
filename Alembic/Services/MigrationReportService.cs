using System.Globalization;
using System.Text;
using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Models;

namespace Alembic.Services;

/// <summary>
/// Tells the client what to change in their own C# by comparing the Draft with the domain the last archive emitted.
/// </summary>
/// <remarks>Only differences decidable by comparing two Drafts are reported; judging whether a change is better belongs to the coherence pass.</remarks>
public class MigrationReportService : IMigrationReportService
{
    /// <inheritdoc />
    /// <remarks>Without a baseline every intent and agent reads as newly added.</remarks>
    public MigrationReport Build(DomainDraft draft)
    {
        // A greenfield domain has no earlier archive: the empty baseline makes everything new.
        DomainDraft baseline = draft.Baseline ?? new DomainDraft();
        List<MigrationEntry> entries = [];

        // The map is compared first and the agents with their toolkits after it.
        CompareIntents(draft, baseline, entries);
        CompareAgents(draft, baseline, entries);

        // Ordered by what it costs to act on: a removed tool or a changed signature is work and a revised sentence is a re-read.
        List<MigrationEntry> sequence = [.. entries.OrderBy(e => e.Change).ThenBy(e => e.Where, StringComparer.Ordinal)];

        // The report carries the findings together with the Markdown the client downloads.
        return new MigrationReport(draft.Baseline?.ImportedFrom, sequence, Render(draft, sequence));
    }

    /// <summary>Reports the intents added, removed or reworded since the baseline.</summary>
    /// <param name="entries">Receives the findings.</param>
    private static void CompareIntents(DomainDraft draft, DomainDraft baseline, List<MigrationEntry> entries)
    {
        // The fallback intent is the classifier's and no agent answers it, so it is never reported.
        foreach (IntentDraft intent in draft.Intents.Where(i =>
                     !string.IsNullOrWhiteSpace(i.Name) && !string.Equals(i.Name, DomainDraft.FallbackIntent, StringComparison.OrdinalIgnoreCase)))
        {
            // The same intent as the last archive emitted it; a miss means the intent is new.
            IntentDraft? baselineIntent = Find(baseline.Intents, intent.Name);

            // A new intent needs an agent class and changes what the classifier weighs.
            if (baselineIntent is null)
            {
                entries.Add(new MigrationEntry(MigrationKind.Intent, intent.Name!, MigrationChange.Added,
                    "New intent. Its agent class must be registered by the plugin and the classifier now weighs this description against every other."));
                continue;
            }

            // A reworded description only changes routing.
            if (!string.Equals(baselineIntent.Description, intent.Description, StringComparison.Ordinal))
                entries.Add(new MigrationEntry(MigrationKind.Intent, intent.Name!, MigrationChange.Revised,
                    "The classifier's description changed. Nothing to compile; routing behaviour changes from the next conversation."));
        }

        // An intent in the baseline that the draft no longer has leaves its agent class without an intent.
        entries.AddRange(
            baseline.Intents.Where(i => !string.IsNullOrWhiteSpace(i.Name)
                                         && !string.Equals(i.Name, DomainDraft.FallbackIntent, StringComparison.OrdinalIgnoreCase)
                                         && Find(draft.Intents, i.Name) is null)
                            .Select(gone => new MigrationEntry(MigrationKind.Intent, gone.Name!, MigrationChange.Removed, "Removed. Any [HandlesIntent] agent still declaring it fails startup: HandlesIntentAgentRegistryService checks the pairing in both directions.")));
    }

    /// <summary>Reports the agents added, reworded or removed since the baseline together with their toolkits and workflows.</summary>
    /// <param name="entries">Receives the findings.</param>
    private static void CompareAgents(DomainDraft draft, DomainDraft baseline, List<MigrationEntry> entries)
    {
        foreach (AgentDraft agent in draft.Agents.Where(a => !string.IsNullOrWhiteSpace(a.ID)))
        {
            // The same agent as the last archive emitted it; a miss means the agent is new.
            AgentDraft? baselineAgent = Find(baseline.Agents, agent.ID);

            // A new agent brings its generated classes, which do not exist in the client's tree yet.
            if (baselineAgent is null)
            {
                entries.Add(new MigrationEntry(MigrationKind.Agent, agent.ID!, MigrationChange.Added,
                    $"New agent. Its generated class and {(agent.Tools.Count > 0 ? "tool class are" : "class is")} in this archive and do not exist in your tree yet."));
            }
            // Reworded prose lives in agents.json alone, so no code is involved.
            else if (Prose(baselineAgent) != Prose(agent))
            {
                entries.Add(new MigrationEntry(MigrationKind.Agent, agent.ID!, MigrationChange.Revised,
                    "Prose changed. It lives entirely in agents.json — replace the file and the change is live, with nothing to rebuild."));
            }

            // A colleague lives in the agent class, so it costs a re-emitted file where prose costs none.
            // The baseline carries no colleagues from before this sitting, so only those declared in it show up.
            if (baselineAgent is not null && !Same(baselineAgent.Code.Consults, agent.Code.Consults))
                entries.Add(new MigrationEntry(MigrationKind.Agent, agent.ID!, MigrationChange.Revised,
                    $"Colleagues changed to {PeerNaming.Describe(agent.Code.Consults)}. "
                    + "[ConsultsAgent] lives on the agent class: re-emit it and check the intent it names is one your plugin registers."));

            // A colleague at another system needs an address and a key that the archive never carries.
            // It is reported every time since a missing or misspelled system entry fails startup.
            entries.AddRange(
                agent.Code.Consults.Where(c => c.Instance is not null)
                                   .Select(colleague => new MigrationEntry(MigrationKind.Agent, agent.ID!, MigrationChange.Revised, $"Consults '{colleague.Intent}' at system '{colleague.Instance}'. Declare that system under " + $"Morgana:AgentToAgent:Partners with its Url, the key you share with it and \"OutboundPolicy\": {{ \"Enabled\": true }}, under exactly the name " + $"'{colleague.Instance}' — spelling and spacing included, or startup refuses the agent. Neither the " + "address nor the key is in this archive. That the system really publishes an agent for " + $"'{colleague.Intent}' is its own card's word, read on the first consultation: a mistake there is a " + "warning at run time and the colleague quietly missing, not a startup error.")));

            // The toolkit and the workflows are compared agent by agent against the same baseline agent.
            CompareTools(agent, baselineAgent, entries);
            CompareWorkflows(agent, baselineAgent, entries);
        }

        // An agent in the baseline that the draft no longer has leaves its classes without an intent.
        entries.AddRange(
            baseline.Agents.Where(a => !string.IsNullOrWhiteSpace(a.ID)
                                         && Find(draft.Agents, a.ID) is null)
                           .Select(gone => new MigrationEntry(MigrationKind.Agent, gone.ID!, MigrationChange.Removed, "Removed from the configuration. Delete its agent and tool classes: an agent class with no intent behind it fails startup.")));
    }

    /// <summary>Whether two colleague lists name the same colleagues, whatever their order or casing.</summary>
    private static bool Same(IEnumerable<string> baseline, IEnumerable<string> revision) =>
        new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase)
            .SetEquals(new HashSet<string>(revision, StringComparer.OrdinalIgnoreCase));

    /// <summary>Whether two colleague sets name the same colleagues, instances included.</summary>
    private static bool Same(
        IEnumerable<Morgana.AI.Records.PeerReference> baseline,
        IEnumerable<Morgana.AI.Records.PeerReference> revision) =>
        Same(baseline.Select(PeerNaming.Describe), revision.Select(PeerNaming.Describe));

    /// <summary>Reports the tools of one agent that were added, changed or removed, with the signature changes that the client's compiled half depends on.</summary>
    /// <param name="baselineAgent">The same agent at the baseline or <c>null</c> when the agent is new, whose tools its own entry already covers.</param>
    /// <param name="entries">Receives the findings.</param>
    private static void CompareTools(AgentDraft agent, AgentDraft? baselineAgent, List<MigrationEntry> entries)
    {
        // A new agent has no baseline tools, so each of its tools is a miss below.
        List<ToolDraft> baselineTools = baselineAgent?.Tools ?? [];

        // A tool with no name is a validation error that the client has yet to fix.
        foreach (ToolDraft tool in agent.Tools.Where(t => !string.IsNullOrWhiteSpace(t.Name)))
        {
            // The tool is identified in the report as agent.tool.
            string where = $"{agent.ID}.{tool.Name}";

            // The same tool as the last archive emitted it; a miss means the tool is new.
            ToolDraft? baselineTool = baselineTools.FirstOrDefault(t => string.Equals(t.Name, tool.Name, StringComparison.Ordinal));

            // A new tool on an existing agent breaks the build until the client implements it.
            if (baselineTool is null)
            {
                // On a new agent the agent's own entry already says that everything in it is new.
                if (baselineAgent is not null)
                    entries.Add(new MigrationEntry(MigrationKind.Signature, where, MigrationChange.Added,
                        $"New tool. The generated half declares `public partial Task<{CodeEmitService.ResultTypeName(tool.Name!)}> {tool.Name}({Signature(tool)})` with its record and the file will not compile until you implement the method in the half you own."));

                continue;
            }

            // The returned record is compared apart from the parameters since it changes different code in the client's half.
            bool returnsChanged = ReturnsChanged(baselineTool, tool);

            // A changed parameter list makes the two parts of the partial method disagree.
            if (Signature(baselineTool) != Signature(tool))
                entries.Add(new MigrationEntry(MigrationKind.Signature, where, MigrationChange.SignatureChanged,
                    $"`{Signature(baselineTool)}` became `{Signature(tool)}`. The generated declaration moves on its own: change your implementation to match, since the two parts of a partial method must agree to compile."));
            // A reworded description is an attribute in the generated half and needs no client change.
            else if (!string.Equals(baselineTool.Description, tool.Description, StringComparison.Ordinal)
                     || baselineTool.Parameters.Zip(tool.Parameters).Any(p => !string.Equals(p.First.Description, p.Second.Description, StringComparison.Ordinal))
                     || (!returnsChanged && baselineTool.Returns.Zip(tool.Returns).Any(r => !string.Equals(r.First.Description, r.Second.Description, StringComparison.Ordinal))))
                entries.Add(new MigrationEntry(MigrationKind.Tool, where, MigrationChange.Revised,
                    "Description changed. It is an attribute in the generated half, so overwriting that file is all it takes."));

            // The record is declared in the generated half and built in the client's, so only the client can bring that code in line.
            if (returnsChanged)
                entries.Add(new MigrationEntry(MigrationKind.Signature, where, MigrationChange.SignatureChanged,
                    $"What it hands back changed: `{Fields(baselineTool)}` became `{Fields(tool)}`. The generated half declares `{CodeEmitService.ResultTypeName(tool.Name!)}` with the new fields: change the code in your half that builds it to match."));

            // Reported on its own entry since a client reading only the signature entry would miss that a parameter now comes from the context or is shared.
            if (baselineTool.Parameters.Zip(tool.Parameters).Any(p => !string.Equals(p.First.Scope, p.Second.Scope, StringComparison.Ordinal) || p.First.Shared != p.Second.Shared))
                entries.Add(new MigrationEntry(MigrationKind.Tool, where, MigrationChange.Revised,
                    "A parameter's scope or sharing changed. It is an attribute on the parameter in the generated half, so overwriting that file is all it takes."));

            // Reported on its own entry since a client reading only the signature entry would miss that the tool now waits for the user.
            if (baselineTool.RequiresExecutionApproval != tool.RequiresExecutionApproval)
                entries.Add(new MigrationEntry(MigrationKind.Tool, where, MigrationChange.Revised,
                    tool.RequiresExecutionApproval
                        ? "Now waits for the user's approval before it runs. It is an attribute in the generated half, so overwriting that file is all it takes."
                        : "No longer waits for the user's approval before it runs. It is an attribute in the generated half, so overwriting that file is all it takes."));

            // Reported on its own entry since the button is an attribute of the method that the signature entry does not mention.
            if (baselineTool.Offering != tool.Offering)
                entries.Add(new MigrationEntry(MigrationKind.Tool, where, MigrationChange.Revised,
                    (tool.Offering, baselineTool.Offering) switch
                    {
                        // The tool never stood on the menu before.
                        ({ } now, null) => string.Create(CultureInfo.InvariantCulture, $"Now offered on the agent's menu as button {now.Ordinal}, '{now.Label}'."),
                        // The tool left the menu.
                        (null, _) => "No longer offered on the agent's menu.",
                        // The tool stays on the menu with another number or another label.
                        ({ } now, { } before) => string.Create(CultureInfo.InvariantCulture, $"Now on the agent's menu as button {now.Ordinal}, '{now.Label}' instead of button {before.Ordinal}, '{before.Label}'.")
                    } + " It is an attribute in the generated half, so overwriting that file is all it takes."));
        }

        // A baseline tool that the draft no longer has leaves an orphan method in the client's half.
        entries.AddRange(
            baselineTools.Where(t => !string.IsNullOrWhiteSpace(t.Name)
                                && !agent.Tools.Any(x => string.Equals(x.Name, t.Name, StringComparison.Ordinal)))
                  .Select(gone => new MigrationEntry(MigrationKind.Signature, $"{agent.ID}.{gone.Name}", MigrationChange.Removed, $"Gone from the configuration. Its generated declaration and record disappear, so `{gone.Name}` in the half you own becomes an orphan method — delete it or the partial no longer matches.")));
    }

    /// <summary>Reports the workflows of one agent that were added, changed or removed, each a regenerated class and so a rebuild of the plugin.</summary>
    private static void CompareWorkflows(AgentDraft agent, AgentDraft? baselineAgent, List<MigrationEntry> entries)
    {
        // A new agent is covered by its own entry, which says that everything in it is new.
        if (baselineAgent is null)
            return;

        // A workflow with no name has no class to write and is a validation error.
        foreach (WorkflowDraft workflow in agent.Workflows.Where(w => !string.IsNullOrWhiteSpace(w.Name)))
        {
            // The workflow is identified in the report as agent.workflow.
            string where = $"{agent.ID}.{workflow.Name}";

            // The same workflow as the last archive emitted it; a miss means the workflow is new.
            WorkflowDraft? baselineWorkflow = baselineAgent.Workflows.FirstOrDefault(w => string.Equals(w.Name, workflow.Name, StringComparison.Ordinal));

            // A new workflow is a new generated class.
            if (baselineWorkflow is null)
                entries.Add(new MigrationEntry(MigrationKind.Workflow, where, MigrationChange.Added,
                    $"New workflow. Its class {workflow.Name}Workflow is generated, so the plugin needs rebuilding."));
            // A workflow whose steps or edges differ is a regenerated class.
            else if (Serialized(baselineWorkflow) != Serialized(workflow))
                entries.Add(new MigrationEntry(MigrationKind.Workflow, where, MigrationChange.SignatureChanged,
                    $"Workflow changed. Its class {workflow.Name}Workflow is regenerated, so the plugin needs rebuilding."));
        }

        // A baseline workflow that the draft no longer has leaves its class out of the archive.
        entries.AddRange(
            baselineAgent.Workflows.Where(w => !string.IsNullOrWhiteSpace(w.Name)
                                     && !agent.Workflows.Any(x => string.Equals(x.Name, w.Name, StringComparison.Ordinal)))
                         .Select(gone => new MigrationEntry(MigrationKind.Workflow, $"{agent.ID}.{gone.Name}", MigrationChange.Removed,
                             $"Workflow removed. Its class {gone.Name}Workflow is no longer emitted, so delete that file from the plugin and rebuild it.")));
    }

    /// <summary>A workflow as agents.json would carry it, so two states compare as the framework reads them.</summary>
    private static string Serialized(WorkflowDraft workflow) =>
        JsonSerializer.Serialize(DraftProjection.ToWorkflowDefinition(workflow));

    /// <summary>Whether the names or the types of the fields a tool hands back differ between its baseline and its current state.</summary>
    private static bool ReturnsChanged(ToolDraft baselineTool, ToolDraft tool) =>
        !baselineTool.Returns.Select(r => (r.Name, r.Type)).ToHashSet().SetEquals(tool.Returns.Select(r => (r.Name, r.Type)));

    /// <summary>Shows the fields a tool hands back as the report quotes them, each with its type.</summary>
    private static string Fields(ToolDraft tool) =>
        tool.Returns.Count == 0
            ? "(nothing declared)"
            : string.Join(", ", tool.Returns.Select(r => $"{r.Name}: {r.Type ?? "?"}"));

    /// <summary>Writes the findings as the <c>MIGRATION.md</c> of the archive.</summary>
    /// <param name="entries">The findings, already ordered by what they cost to act on.</param>
    private static string Render(DomainDraft draft, IReadOnlyList<MigrationEntry> entries)
    {
        // The report is a Markdown document that ships in the archive beside the code.
        StringBuilder sb = new StringBuilder();

        sb.AppendLine("# Migration report");
        sb.AppendLine();

        // Without an earlier archive there is nothing to compare so the client is told how to start.
        if (draft.Baseline is null)
        {
            sb.AppendLine("No earlier archive came with this domain, so there is nothing to compare against:");
            sb.AppendLine("everything in this archive is new. Drop it into a plugin project, point Morgana's");
            sb.AppendLine("`Morgana:Plugins:Directories` at the build output and start.");
            return sb.ToString();
        }

        // The report says which earlier archive it compares against.
        sb.AppendLine(CultureInfo.InvariantCulture, $"Against {draft.Baseline.ImportedFrom}.");
        sb.AppendLine();

        // An unchanged domain is reported too, so the client learns that the report is always there.
        if (entries.Count == 0)
        {
            sb.AppendLine("Nothing changed. The `agents.json` in this archive is equivalent to that one,");
            sb.AppendLine("and the generated sources match the code you already have.");
            return sb.ToString();
        }

        // The preface tells the client that the changes are theirs to apply.
        sb.AppendLine("Alembic never sees your tree, so nothing here is applied for you. What follows is every");
        sb.AppendLine("difference, ordered by what it costs to act on.");
        sb.AppendLine();

        // One section per kind of change, most costly first.
        foreach (IGrouping<MigrationChange, MigrationEntry> group in entries.GroupBy(e => e.Change).OrderBy(g => g.Key))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {Heading(group.Key)}");
            sb.AppendLine();

            // Each finding is a bullet naming where it is and what to do.
            foreach (MigrationEntry entry in group)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- **`{entry.Where}`** ({entry.Kind.ToString().ToLowerInvariant()}) — {entry.Detail}");

            sb.AppendLine();
        }

        // The closing section repeats who owns which half of a tool class.
        sb.AppendLine("## The two halves");
        sb.AppendLine();
        sb.AppendLine("Every `*.g.cs` in this archive is Alembic's and is regenerated in full — overwrite yours.");
        sb.AppendLine("Every matching `*.cs` is yours: Alembic wrote it once as a working mock and will not write it");
        sb.AppendLine("again. Where a signature above changed, that is the file to edit.");

        // The Markdown becomes the MIGRATION.md entry.
        return sb.ToString();
    }

    /// <summary>The heading of a change group, which says what the client does with its entries.</summary>
    private static string Heading(MigrationChange change) => change switch
    {
        MigrationChange.Removed => "Removed — delete the code behind these",
        MigrationChange.SignatureChanged => "Signatures changed — edit the half you own",
        MigrationChange.Added => "New — this code does not exist in your tree yet",
        _ => "Reworded — configuration only, nothing to rebuild"
    };

    /// <summary>A tool's parameter list as the generated declaration renders it, for comparing two states of the tool.</summary>
    /// <remarks>It does not share the emitter's rendering so a formatting change there cannot alter what counts as a signature change.</remarks>
    private static string Signature(ToolDraft tool) =>
        string.Join(", ", tool.Parameters
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p.Required ? $"string {p.Name}" : $"string? {p.Name} = null"));

    /// <summary>An agent's four prose sections joined, to tell whether any of them was reworded.</summary>
    private static string Prose(AgentDraft agent) =>
        string.Join("", agent.Target, agent.Instructions, agent.Personality, agent.Formatting);

    /// <summary>Finds an intent by name whatever its casing, which the classifier ignores too.</summary>
    private static IntentDraft? Find(List<IntentDraft> intents, string? name) =>
        intents.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds an agent by its intent name whatever its casing.</summary>
    private static AgentDraft? Find(List<AgentDraft> agents, string? id) =>
        agents.FirstOrDefault(a => string.Equals(a.ID, id, StringComparison.OrdinalIgnoreCase));
}
