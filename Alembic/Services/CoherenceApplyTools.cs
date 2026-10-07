using System.ComponentModel;
using Alembic.Model;
using Morgana.AI;
using Morgana.AI.Attributes;

namespace Alembic.Services;

/// <summary>
/// The tools Alembic calls while applying one coherence finding.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="InterviewTools"/>, which writes into the single agent the current interview
/// pass has open, these write directly into agents already committed to the Draft — the ones the
/// finding named in its <c>Where</c>. There is no client dialogue here: the finding's own
/// <c>Fix</c> is the instruction, already read and accepted by pressing the button that started
/// this pass.
/// </para>
/// <para>
/// Deliberately narrower than the interview's toolset. No <c>SetAgentPersonality</c>: no coherence
/// defect this pass exists for is about voice and a fix that touched it would be rewriting
/// something nobody asked to change. No <c>DeclareIntent</c>/<c>DropIntent</c>: the map is not
/// reopened here any more than it is mid-interview, only its description may be sharpened.
/// </para>
/// </remarks>
public class CoherenceApplyTools
{
    private readonly DomainDraft draft;

    /// <summary>
    /// What changed, in the client's own words, recorded by <see cref="ApplyCompleted"/>.
    /// </summary>
    public string? Summary { get; private set; }

    /// <summary>
    /// Binds the toolset to the domain the finding was read against.
    /// </summary>
    public CoherenceApplyTools(DomainDraft draft)
    {
        this.draft = draft;
    }

    /// <summary>
    /// Returns one agent's prose and toolkit as they stand, by ID.
    /// </summary>
    /// <remarks>
    /// Called before any write: this pass opens knowing only the finding's own words and a tool
    /// call naming a field it has not seen the rest of risks discarding what a sibling field
    /// depended on.
    /// </remarks>
    [Description("Returns one agent's prose and toolkit as they stand, by its ID. Call it for every agent the finding names before changing anything.")]
    [RequiresApproval(false)]
    public ToolReply GetAgent(
        [Description("The agent's ID, exactly as the finding's Where names it.")] [ToolParameter(Records.ToolScope.Request)] string agentId)
    {
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused($"No agent named '{agentId}' in this domain. The finding's Where names agents by their intent ID.");

        List<string> sections =
        [
            agent.Target ?? "(no target)",
            agent.Personality ?? "(no personality)",
            agent.Instructions ?? "(no instructions)",
            agent.Formatting ?? "(no formatting)"
        ];

        string tools = agent.Tools.Count == 0
            ? "Declares no native tools."
            : "Tools:\n" + string.Join("\n", agent.Tools.Select(t =>
                $"- {t.Name}{(t.RequiresExecutionApproval ? " (waits for the user's approval)" : string.Empty)}: {t.Description}"
                + string.Concat(t.Parameters.Select(p =>
                    $"\n    {p.Name} [{p.Scope ?? "no scope yet"}{(p.Required ? "" : ", optional")}{(p.Shared ? ", shared" : "")}]: {p.Description}"))));

        // The workflows are read-only here: a procedure written in prose is fixed in the prose, with
        // the workflow set beside it as the fact the sentence must not restate.
        return new ToolReply(string.Join("\n\n", sections) + "\n\n" + tools + "\n\n" + InterviewTools.DescribeWorkflows(agent.Workflows));
    }

    /// <summary>
    /// Records an agent's Target section.
    /// </summary>
    [Description("Replaces the named agent's whole Target section.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentTarget(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("The complete revised Target, addressed to the agent as 'you'. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string target)
    {
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused($"Nothing recorded: no agent named '{agentId}'.");

        agent.Target = target?.Trim();
        MarkRevised(agent);
        return new ToolReply($"{agentId}'s Target revised.");
    }

    /// <summary>
    /// Records an agent's Instructions section.
    /// </summary>
    [Description("Replaces the named agent's whole Instructions section.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentInstructions(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("The complete revised Instructions, addressed to the agent as 'you'. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string instructions)
    {
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused($"Nothing recorded: no agent named '{agentId}'.");

        agent.Instructions = instructions?.Trim();
        MarkRevised(agent);
        return new ToolReply($"{agentId}'s Instructions revised.");
    }

    /// <summary>
    /// Records an agent's Formatting section.
    /// </summary>
    [Description("Replaces the named agent's whole Formatting section.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentFormatting(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("The complete revised Formatting, addressed to the agent as 'you'. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string formatting)
    {
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused($"Nothing recorded: no agent named '{agentId}'.");

        agent.Formatting = formatting?.Trim();
        MarkRevised(agent);
        return new ToolReply($"{agentId}'s Formatting revised.");
    }

    /// <summary>
    /// Sharpens an intent's description — the one intent-level edit a coherence fix ever makes.
    /// </summary>
    /// <remarks>
    /// Everything else about the map is out of reach on purpose: an overlapping-intents finding is
    /// resolved by telling two descriptions apart, never by adding, dropping or renaming an entry.
    /// </remarks>
    [Description("Sharpens one intent's description — the only edit a coherence fix ever makes to the map itself.")]
    [RequiresApproval(false)]
    public ToolReply SetIntentDescription(
        [Description("The intent's name, exactly as it appears in the domain.")] [ToolParameter(Records.ToolScope.Request)] string intentName,
        [Description("The complete revised description, the classifier's whole basis for routing here.")] [ToolParameter(Records.ToolScope.Request)] string description)
    {
        IntentDraft? intent = draft.Intents.FirstOrDefault(i =>
            string.Equals(i.Name, intentName?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (intent is null)
            return ToolReply.Refused($"Nothing recorded: no intent named '{intentName}'.");

        intent.Description = description?.Trim();

        if (intent.Origin == Provenance.Imported)
            intent.Origin = Provenance.Revised;

        return new ToolReply($"'{intentName}' description revised.");
    }

    /// <summary>
    /// Opens a tool on the named agent, or revises the description and the approval requirement of one already open.
    /// </summary>
    /// <remarks>
    /// Touches only the tool's name, description and approval requirement. Parameters are a separate concern reached
    /// through <see cref="SetToolParameter"/> — a revision here never disturbs a parameter list
    /// already recorded on the same tool, whether the tool is new or already existed.
    /// </remarks>
    [Description("Opens a tool on the named agent, or revises one already open.")]
    [RequiresApproval(false)]
    public ToolReply DeclareTool(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("PascalCase, a verb the agent performs.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("What the tool does, what it returns and when to call it.")] [ToolParameter(Records.ToolScope.Request)] string description,
        [Description("Whether the tool changes something real and so waits for the user's approval. Keep what GetAgent shows unless the Fix says otherwise.")] [ToolParameter(Records.ToolScope.Request)] bool requiresApproval)
    {
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused($"No tool recorded: no agent named '{agentId}'.");

        string cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
            return ToolReply.Refused("No tool recorded: a tool must have a name.");

        ToolDraft? existing = FindTool(agent, cleanName);
        bool revision = existing is not null;
        ToolDraft tool = existing ?? new ToolDraft { Name = cleanName, Origin = Provenance.Authored };
        tool.Description = description?.Trim();
        tool.RequiresExecutionApproval = requiresApproval;

        if (!revision)
            agent.Tools.Add(tool);

        MarkRevised(agent);

        string complaint = InterviewTools.IdentifierComplaint(cleanName, "tool name", pascalCase: true);
        return new ToolReply((revision ? $"'{cleanName}' revised on {agentId}." : $"'{cleanName}' declared on {agentId}.")
               + (complaint.Length > 0 ? " " + complaint : string.Empty));
    }

    /// <summary>
    /// Adds a parameter to a declared tool of the named agent, or revises one already there.
    /// </summary>
    /// <remarks>
    /// Touches exactly the one parameter named — every other parameter already on the tool and
    /// the tool's own name and description, are left as they stand. The tool itself must already
    /// exist: this never creates one, since a coherence finding names a tool it has already read.
    /// </remarks>
    [Description("Adds a parameter to a declared tool on the named agent, or revises one already there by name and in place.")]
    [RequiresApproval(false)]
    public ToolReply SetToolParameter(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("The exact name of a tool already declared on that agent.")] [ToolParameter(Records.ToolScope.Request)] string toolName,
        [Description("camelCase parameter name.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("What the value is and what a good one looks like.")] [ToolParameter(Records.ToolScope.Request)] string description,
        [Description("'request' or 'context'.")] [ToolParameter(Records.ToolScope.Request)] string scope,
        [Description("Whether the agent must supply this value on every call.")] [ToolParameter(Records.ToolScope.Request)] bool required,
        [Description("Whether the resolved value is published to the whole conversation. Only a 'context' parameter may be shared.")] [ToolParameter(Records.ToolScope.Request)] bool shared)
    {
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused($"No parameter recorded: no agent named '{agentId}'.");

        if (FindTool(agent, toolName) is not { } tool)
            return ToolReply.Refused($"No parameter recorded: no tool named '{toolName}' on {agentId}.");

        string cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
            return ToolReply.Refused("No parameter recorded: a parameter must have a name.");

        // An unrecognised scope passes through verbatim so that it surfaces as a validation finding
        // rather than being silently coerced into one of the two known ones.
        string? resolvedScope = InterviewTools.ResolveScope(scope);

        ToolParameterDraft? existing = tool.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, cleanName, StringComparison.Ordinal));

        bool revision = existing is not null;
        ToolParameterDraft parameter = existing ?? new ToolParameterDraft { Name = cleanName };

        parameter.Description = description?.Trim();
        parameter.Scope = resolvedScope;
        parameter.Required = required;
        parameter.Shared = shared;

        if (!revision)
            tool.Parameters.Add(parameter);

        MarkRevised(agent);

        List<string> complaints = InterviewTools.ScopeComplaints(resolvedScope, required, shared);

        return new ToolReply($"'{cleanName}' recorded on {agentId}.{tool.Name}."
               + (complaints.Count > 0 ? " " + string.Join(" ", complaints) : string.Empty));
    }

    /// <summary>
    /// Removes a parameter from a tool of the named agent.
    /// </summary>
    [Description("Removes a parameter from a tool on the named agent.")]
    [RequiresApproval(false)]
    public ToolReply DropToolParameter(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("The exact name of the tool.")] [ToolParameter(Records.ToolScope.Request)] string toolName,
        [Description("The exact name of the parameter to remove.")] [ToolParameter(Records.ToolScope.Request)] string parameterName)
    {
        if (Find(agentId) is not { } agent || FindTool(agent, toolName) is not { } tool)
            return ToolReply.Refused($"Nothing dropped: no such tool on '{agentId}'.");

        int removed = tool.Parameters.RemoveAll(p =>
            string.Equals(p.Name, parameterName?.Trim(), StringComparison.Ordinal));

        if (removed > 0)
            MarkRevised(agent);

        return removed > 0
            ? new ToolReply($"'{parameterName}' dropped from {agentId}.{tool.Name}.")
            : ToolReply.Refused($"Nothing dropped: {tool.Name} has no parameter named '{parameterName}'.");
    }

    /// <summary>
    /// Removes a tool and everything on it from the named agent.
    /// </summary>
    [Description("Removes a tool and every parameter on it from the named agent — for when a duplicated reach is resolved by keeping only one shape.")]
    [RequiresApproval(false)]
    public ToolReply DropTool(
        [Description("The agent's ID.")] [ToolParameter(Records.ToolScope.Request)] string agentId,
        [Description("The exact name of the tool to remove.")] [ToolParameter(Records.ToolScope.Request)] string toolName)
    {
        if (Find(agentId) is not { } agent)
            return ToolReply.Refused($"Nothing dropped: no agent named '{agentId}'.");

        int removed = agent.Tools.RemoveAll(t =>
            string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));

        if (removed > 0)
            MarkRevised(agent);

        return removed > 0
            ? new ToolReply($"'{toolName}' dropped from {agentId}, with its parameters.")
            : ToolReply.Refused($"Nothing dropped: {agentId} has no tool named '{toolName}'.");
    }

    /// <summary>
    /// Declares the fix applied.
    /// </summary>
    [Description("Declares the fix applied. Call exactly once, last.")]
    [RequiresApproval(false)]
    public ToolReply ApplyCompleted(
        [Description("One sentence, in the domain's own words, naming the field and the agent changed.")] [ToolParameter(Records.ToolScope.Request)] string summary)
    {
        Summary = string.IsNullOrWhiteSpace(summary) ? "Applied." : summary.Trim();
        return new ToolReply("Recorded.");
    }

    /// <summary>
    /// Flags an agent touched by this pass so the migration report tells the client honestly which
    /// of their imported agents a coherence fix reached.
    /// </summary>
    private static void MarkRevised(AgentDraft agent)
    {
        if (agent.Origin == Provenance.Imported)
            agent.Origin = Provenance.Revised;
    }

    /// <summary>
    /// Looks up an agent already committed to the Draft, by its intent ID — case-insensitively,
    /// since that is how the finding's own <c>Where</c> names it.
    /// </summary>
    private AgentDraft? Find(string? agentId) =>
        draft.Agents.FirstOrDefault(a => string.Equals(a.ID, agentId?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Looks up one of an agent's already-declared tools, by name — ordinally, the same comparison
    /// <see cref="DraftValidationService"/> holds tool names to.
    /// </summary>
    private static ToolDraft? FindTool(AgentDraft agent, string? toolName) =>
        agent.Tools.FirstOrDefault(t => string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));
}
