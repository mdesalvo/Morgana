using System.ComponentModel;
using Alembic.Interfaces;
using Alembic.Models;
using Alembic.Services;
using Morgana.AI;
using Morgana.AI.Attributes;

namespace Alembic.Tools;

/// <summary>
/// The tools that write one coherence fix straight into agents already committed to the draft.
/// </summary>
/// <remarks>Voice and the map are out of reach: only the sections, the toolkit and an intent's description can change.</remarks>
public class CoherenceApplyTools
{
    /// <summary>The domain the finding was read against and the fix is written into.</summary>
    private readonly DomainDraft draft;

    /// <summary>The authority on <c>alembic.json</c>, which holds every sentence these tools answer with.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>What the fix changed, as the client is told once the pass ends.</summary>
    public string? Summary { get; private set; }

    /// <summary>
    /// Builds the toolset over the domain the finding was read against.
    /// </summary>
    public CoherenceApplyTools(DomainDraft draft, IAlembicPromptService alembicPromptService)
    {
        this.draft = draft;
        this.alembicPromptService = alembicPromptService;
    }

    /// <summary>
    /// Fetches the sentence a tool answers the model with, from the <c>ToolInjections</c> of the <c>Alembic</c> prompt.
    /// </summary>
    private string Said(string injectionName, params (string Placeholder, string Value)[] values) =>
        alembicPromptService.ResolveToolInjection(injectionName, values);

    /// <summary>
    /// Returns one agent's prose and toolkit as they stand, by ID.
    /// </summary>
    /// <remarks>The pass knows only the finding's words when it opens: a write made before this read can discard what a sibling section depended on.</remarks>
    [Description("Returns one agent's prose and toolkit as they stand, by its ID. Call it for every agent the finding names before changing anything.")]
    public ToolReply GetAgent(
        [Description("The agent's ID, exactly as the finding's Where names it.")] [ToolParameter(Records.ToolScope.Request)] string agentId)
    {
        // An ID that matches no committed agent is refused, so the model corrects the name before reading anything.
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused(Said("ApplyAgentUnknown", ("agent", agentId ?? string.Empty)));

        // The four sections are shown in the order the agent's prompt carries them, a missing one flagged as not written.
        List<string> sections =
        [
            agent.Target ?? "(no target)",
            agent.Personality ?? "(no personality)",
            agent.Instructions ?? "(no instructions)",
            agent.Formatting ?? "(no formatting)"
        ];

        // Each tool is listed with its approval requirement and every parameter with its scope and flags, so the model reads the toolkit whole.
        string tools = agent.Tools.Count == 0
            ? Said("NoNativeTools")
            : Said("ToolsHeader") + "\n" + string.Join("\n", agent.Tools.Select(t =>
                $"- {t.Name}{(t.RequiresExecutionApproval ? " " + Said("ToolWaitsForApproval") : string.Empty)}: {t.Description}"
                + string.Concat(t.Parameters.Select(p =>
                    $"\n    {p.Name} [{p.Scope ?? Said("NoScopeYet")}{(p.Required ? "" : ", " + Said("ParameterOptional"))}{(p.Shared ? ", " + Said("ParameterShared") : "")}]: {p.Description}"))));

        // The workflows are read-only here: a procedure written in prose is fixed in the prose, with
        // the workflow set beside it as the fact the sentence must not restate.
        // The reply hands over the sections, the toolkit and the workflows as one reading of the agent.
        return new ToolReply(string.Join("\n\n", sections) + "\n\n" + tools + "\n\n" + InterviewTools.DescribeWorkflows(alembicPromptService, agent.Workflows));
    }

    /// <summary>
    /// Records an agent's Target section.
    /// </summary>
    [Description("Replaces the named agent's whole Target section.")]
    public ToolReply SetAgentTarget(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("The complete revised Target, addressed to the agent as 'you'. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string target)
    {
        // A section can only be rewritten on an agent the finding named and the draft holds.
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused(Said("ApplyAgentNotNamed", ("agent", agentId ?? string.Empty)));

        // The revised section replaces the whole previous text.
        agent.Target = target?.Trim();

        // An imported agent reached by the fix is reported as revised.
        MarkRevised(agent);

        // The model is told which section of which agent now holds the new text.
        return new ToolReply(Said("ApplySectionRevised", ("agent", agentId), ("section", "Target")));
    }

    /// <summary>
    /// Records an agent's Instructions section.
    /// </summary>
    [Description("Replaces the named agent's whole Instructions section.")]
    public ToolReply SetAgentInstructions(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("The complete revised Instructions, addressed to the agent as 'you'. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string instructions)
    {
        // A section can only be rewritten on an agent the finding named and the draft holds.
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused(Said("ApplyAgentNotNamed", ("agent", agentId ?? string.Empty)));

        // The revised section replaces the whole previous text.
        agent.Instructions = instructions?.Trim();

        // An imported agent reached by the fix is reported as revised.
        MarkRevised(agent);

        // The model is told which section of which agent now holds the new text.
        return new ToolReply(Said("ApplySectionRevised", ("agent", agentId), ("section", "Instructions")));
    }

    /// <summary>
    /// Records an agent's Formatting section.
    /// </summary>
    [Description("Replaces the named agent's whole Formatting section.")]
    public ToolReply SetAgentFormatting(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("The complete revised Formatting, addressed to the agent as 'you'. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string formatting)
    {
        // A section can only be rewritten on an agent the finding named and the draft holds.
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused(Said("ApplyAgentNotNamed", ("agent", agentId ?? string.Empty)));

        // The revised section replaces the whole previous text.
        agent.Formatting = formatting?.Trim();

        // An imported agent reached by the fix is reported as revised.
        MarkRevised(agent);

        // The model is told which section of which agent now holds the new text.
        return new ToolReply(Said("ApplySectionRevised", ("agent", agentId), ("section", "Formatting")));
    }

    /// <summary>
    /// Sharpens an intent's description — the one intent-level edit a coherence fix ever makes.
    /// </summary>
    /// <remarks>An overlapping-intents finding is resolved by telling two descriptions apart: no entry is added, dropped or renamed.</remarks>
    [Description("Sharpens one intent's description — the only edit a coherence fix ever makes to the map itself.")]
    public ToolReply SetIntentDescription(
        [Description("The intent's name, exactly as it appears in the domain.")] [ToolParameter(Records.ToolScope.Request)] string intentName,
        [Description("The complete revised description, the classifier's whole basis for routing here.")] [ToolParameter(Records.ToolScope.Request)] string description)
    {
        // The intent is found by name regardless of case, as the finding quotes it.
        IntentDraft? intent = draft.Intents.FirstOrDefault(i =>
            string.Equals(i.Name, intentName?.Trim(), StringComparison.OrdinalIgnoreCase));

        // A name that matches no intent is refused so the model quotes the domain's own name.
        if (intent is null)
            return ToolReply.Refused(Said("ApplyIntentNotNamed", ("name", intentName ?? string.Empty)));

        // The new description is what the classifier routes on from now on.
        intent.Description = description?.Trim();

        // An imported intent reached by the fix is reported as revised.
        if (intent.Origin == Provenance.Imported)
            intent.Origin = Provenance.Revised;

        // The model is told which intent now carries the sharper description.
        return new ToolReply(Said("ApplyIntentDescriptionRevised", ("name", intentName)));
    }

    /// <summary>
    /// Opens a tool on the named agent, or revises the description and the approval requirement of one already open.
    /// </summary>
    /// <remarks>The parameters already on the tool are left as they stand: they change only through <see cref="SetToolParameter"/>.</remarks>
    [Description("Opens a tool on the named agent, or revises one already open.")]
    public ToolReply DeclareTool(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("PascalCase, a verb the agent performs.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("What the tool does, what it returns and when to call it.")] [ToolParameter(Records.ToolScope.Request)] string description,
        [Description("Whether the tool changes something real and so waits for the user's approval. Keep what GetAgent shows unless the Fix says otherwise.")] [ToolParameter(Records.ToolScope.Request)] bool requiresApproval)
    {
        // A tool is declared only on an agent the draft holds.
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused(Said("ApplyToolAgentMissing", ("agent", agentId ?? string.Empty)));

        // A blank name would declare a tool nobody can call.
        string cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("ApplyToolNameMissing"));

        // A tool of the same name on this agent is revised in place, otherwise a new authored tool is opened.
        ToolDraft? match = FindTool(agent, cleanName);
        bool revision = match is not null;
        ToolDraft tool = match ?? new ToolDraft { Name = cleanName, Origin = Provenance.Authored };

        // The description and the approval requirement are what the model reads and what the user is asked about.
        tool.Description = description?.Trim();
        tool.RequiresExecutionApproval = requiresApproval;

        // Only a new tool joins the agent's toolkit.
        if (!revision)
            agent.Tools.Add(tool);

        // An imported agent reached by the fix is reported as revised.
        MarkRevised(agent);

        // A tool name off the PascalCase convention is flagged in the same reply so the model corrects it at once.
        string complaint = InterviewTools.IdentifierComplaint(alembicPromptService, cleanName, "tool name", pascalCase: true);
        // The reply says whether the tool was opened or revised and appends the naming complaint when there is one.
        return new ToolReply((revision
                   ? Said("ApplyToolRevised", ("name", cleanName), ("agent", agentId))
                   : Said("ApplyToolDeclared", ("name", cleanName), ("agent", agentId)))
               + (complaint.Length > 0 ? " " + complaint : string.Empty));
    }

    /// <summary>
    /// Adds a parameter to a declared tool of the named agent, or revises one already there.
    /// </summary>
    /// <remarks>The tool must already exist: a coherence finding names a tool it has read.</remarks>
    [Description("Adds a parameter to a declared tool on the named agent, or revises one already there by name and in place.")]
    public ToolReply SetToolParameter(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("The exact name of a tool already declared on that agent.")] [ToolParameter(Records.ToolScope.Request)] string toolName,
        [Description("camelCase parameter name.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("What the value is and what a good one looks like. Never say here how a context value is found or when it is asked for: Morgana settles that herself. Never an instruction to the agent about asking the user for the value either: a value the user has not given is asked for by rules the agent already holds.")] [ToolParameter(Records.ToolScope.Request)] string description,
        [Description("'request' or 'context'.")] [ToolParameter(Records.ToolScope.Request)] string scope,
        [Description("Whether the agent must supply this value on every call.")] [ToolParameter(Records.ToolScope.Request)] bool required,
        [Description("Whether the resolved value is published to the whole conversation. Only a 'context' parameter may be shared.")] [ToolParameter(Records.ToolScope.Request)] bool shared)
    {
        // A parameter is written only on an agent the draft holds.
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused(Said("ApplyParameterAgentMissing", ("agent", agentId ?? string.Empty)));

        // The tool must already be declared on that agent: this tool never opens one.
        if (FindTool(agent, toolName) is not { } tool)
            return ToolReply.Refused(Said("ApplyParameterToolMissing", ("tool", toolName ?? string.Empty), ("agent", agentId)));

        // A blank name would declare a parameter nobody can fill.
        string cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("ApplyParameterNameMissing"));

        // An unrecognised scope passes through verbatim so that it surfaces as a validation finding
        // rather than being silently coerced into one of the two known ones.
        string? resolvedScope = InterviewTools.ResolveScope(scope);

        // A parameter of the same name on this tool is revised in place.
        ToolParameterDraft? match = tool.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, cleanName, StringComparison.Ordinal));

        // The reply to the model says whether the parameter was revised or newly opened.
        bool revision = match is not null;
        ToolParameterDraft parameter = match ?? new ToolParameterDraft { Name = cleanName };

        // The parameter takes the description, scope and flags the fix gives it.
        parameter.Description = description?.Trim();
        parameter.Scope = resolvedScope;
        parameter.Required = required;
        parameter.Shared = shared;

        // Only a new parameter joins the tool's list.
        if (!revision)
            tool.Parameters.Add(parameter);

        // An imported agent reached by the fix is reported as revised.
        MarkRevised(agent);

        // Scope and flag combinations the framework refuses at startup are reported back in the same reply.
        List<string> complaints = InterviewTools.ScopeComplaints(alembicPromptService, resolvedScope, required, shared);

        // The reply confirms the parameter on its tool and appends any complaint.
        return new ToolReply(Said("ApplyParameterRecorded", ("name", cleanName), ("agent", agentId), ("tool", tool.Name ?? string.Empty))
               + (complaints.Count > 0 ? " " + string.Join(" ", complaints) : string.Empty));
    }

    /// <summary>
    /// Removes a parameter from a tool of the named agent.
    /// </summary>
    [Description("Removes a parameter from a tool on the named agent.")]
    public ToolReply DropToolParameter(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("The exact name of the tool.")] [ToolParameter(Records.ToolScope.Request)] string toolName,
        [Description("The exact name of the parameter to remove.")] [ToolParameter(Records.ToolScope.Request)] string parameterName)
    {
        // Neither the agent nor its tool can be missing for a removal to mean anything.
        if (Find(agentId) is not { } agent || FindTool(agent, toolName) is not { } tool)
            return ToolReply.Refused(Said("ApplyDropToolUnknown", ("agent", agentId ?? string.Empty)));

        // The parameter is dropped by exact name and the count says whether it was there.
        int removals = tool.Parameters.RemoveAll(p =>
            string.Equals(p.Name, parameterName?.Trim(), StringComparison.Ordinal));

        // Only an actual removal marks an imported agent as revised.
        if (removals > 0)
            MarkRevised(agent);

        // A removal is confirmed while a name that was never there is refused.
        return removals > 0
            ? new ToolReply(Said("ApplyParameterDropped", ("name", parameterName ?? string.Empty), ("agent", agentId), ("tool", tool.Name ?? string.Empty)))
            : ToolReply.Refused(Said("ParameterAbsent", ("tool", tool.Name ?? string.Empty), ("name", parameterName ?? string.Empty)));
    }

    /// <summary>
    /// Removes a tool and everything on it from the named agent.
    /// </summary>
    [Description("Removes a tool and every parameter on it from the named agent — for when a duplicated reach is resolved by keeping only one shape.")]
    public ToolReply DropTool(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("The exact name of the tool to remove.")] [ToolParameter(Records.ToolScope.Request)] string toolName)
    {
        // A tool is dropped only from an agent the draft holds.
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused(Said("ApplyDropAgentMissing", ("agent", agentId ?? string.Empty)));

        // The tool goes with all its parameters and the count says whether it was there.
        int removals = agent.Tools.RemoveAll(t =>
            string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));

        // Only an actual removal marks an imported agent as revised.
        if (removals > 0)
            MarkRevised(agent);

        // A removal is confirmed while a name that was never there is refused.
        return removals > 0
            ? new ToolReply(Said("ApplyToolDropped", ("name", toolName ?? string.Empty), ("agent", agentId)))
            : ToolReply.Refused(Said("ApplyToolAbsent", ("agent", agentId), ("name", toolName ?? string.Empty)));
    }

    /// <summary>
    /// Declares the fix applied.
    /// </summary>
    [Description("Declares the fix applied. Call exactly once, last.")]
    public ToolReply ApplyCompleted(
        [Description("One sentence, in the domain's own words, naming the field and the agent changed.")] [ToolParameter(Records.ToolScope.Request)] string summary)
    {
        // The summary is what the client reads once the pass ends; a blank one falls back to a bare confirmation.
        Summary = string.IsNullOrWhiteSpace(summary) ? "Applied." : summary.Trim();

        // The model is told the fix is recorded and the pass may stop.
        return new ToolReply(Said("ApplyRecorded"));
    }

    /// <summary>
    /// Marks an imported agent as revised so the migration report lists the agents a fix reached.
    /// </summary>
    private static void MarkRevised(AgentDraft agent)
    {
        // An agent authored here is already reported as new: only an imported one changes provenance.
        if (agent.Origin == Provenance.Imported)
            agent.Origin = Provenance.Revised;
    }

    /// <summary>
    /// Finds a committed agent by its intent ID regardless of case, as the finding's <c>Where</c> names it.
    /// </summary>
    private AgentDraft? Find(string? agentId) =>
        draft.Agents.FirstOrDefault(a => string.Equals(a.ID, agentId?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Finds one of an agent's declared tools by exact name, the comparison validation holds tool names to.
    /// </summary>
    private static ToolDraft? FindTool(AgentDraft agent, string? toolName) =>
        agent.Tools.FirstOrDefault(t => string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));
}
