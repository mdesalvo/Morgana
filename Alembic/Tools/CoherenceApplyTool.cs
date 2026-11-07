using System.ComponentModel;
using Alembic.Models;
using Morgana.AI;

namespace Alembic.Tools;

/// <summary>
/// The tools Alembic calls while applying one coherence finding.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="InterviewTool"/>, which writes into the single agent the current interview
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
public class CoherenceApplyTool
{
    private readonly DomainDraft draft;

    /// <summary>
    /// What changed, in the client's own words, recorded by <see cref="ApplyCompleted"/>.
    /// </summary>
    public string? Summary { get; private set; }

    /// <summary>
    /// Binds the toolset to the domain the finding was read against.
    /// </summary>
    public CoherenceApplyTool(DomainDraft draft)
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
    public string GetAgent(
        [Description("The agent's ID, exactly as the finding's Where names it.")] string agentId)
    {
        if (Find(agentId) is not { } agent)
            return $"No agent named '{agentId}' in this domain. The finding's Where names agents by their intent ID.";

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
                $"- {t.Name}: {t.Description}"
                + string.Concat(t.Parameters.Select(p =>
                    $"\n    {p.Name} [{p.Scope ?? "authored"}{(p.Required ? "" : ", optional")}{(p.Shared ? ", shared" : "")}]: {p.Description}"))));

        return string.Join("\n\n", sections) + "\n\n" + tools;
    }

    /// <summary>
    /// Records an agent's Target section.
    /// </summary>
    [Description("Replaces the named agent's whole Target section.")]
    public string SetAgentTarget(
        [Description("The agent's ID.")] string agentId,
        [Description("The complete revised Target, addressed to the agent as 'you'. No section label; that is added for you.")] string target)
    {
        if (Find(agentId) is not { } agent)
            return $"Nothing recorded: no agent named '{agentId}'.";

        agent.Target = target?.Trim();
        MarkRevised(agent);
        return $"{agentId}'s Target revised.";
    }

    /// <summary>
    /// Records an agent's Instructions section.
    /// </summary>
    [Description("Replaces the named agent's whole Instructions section.")]
    public string SetAgentInstructions(
        [Description("The agent's ID.")] string agentId,
        [Description("The complete revised Instructions, addressed to the agent as 'you'. No section label; that is added for you.")] string instructions)
    {
        if (Find(agentId) is not { } agent)
            return $"Nothing recorded: no agent named '{agentId}'.";

        agent.Instructions = instructions?.Trim();
        MarkRevised(agent);
        return $"{agentId}'s Instructions revised.";
    }

    /// <summary>
    /// Records an agent's Formatting section.
    /// </summary>
    [Description("Replaces the named agent's whole Formatting section.")]
    public string SetAgentFormatting(
        [Description("The agent's ID.")] string agentId,
        [Description("The complete revised Formatting, addressed to the agent as 'you'. No section label; that is added for you.")] string formatting)
    {
        if (Find(agentId) is not { } agent)
            return $"Nothing recorded: no agent named '{agentId}'.";

        agent.Formatting = formatting?.Trim();
        MarkRevised(agent);
        return $"{agentId}'s Formatting revised.";
    }

    /// <summary>
    /// Sharpens an intent's description — the one intent-level edit a coherence fix ever makes.
    /// </summary>
    /// <remarks>
    /// Everything else about the map is out of reach on purpose: an overlapping-intents finding is
    /// resolved by telling two descriptions apart, never by adding, dropping or renaming an entry.
    /// </remarks>
    [Description("Sharpens one intent's description — the only edit a coherence fix ever makes to the map itself.")]
    public string SetIntentDescription(
        [Description("The intent's name, exactly as it appears in the domain.")] string intentName,
        [Description("The complete revised description, the classifier's whole basis for routing here.")] string description)
    {
        IntentDraft? intent = draft.Intents.FirstOrDefault(i =>
            string.Equals(i.Name, intentName?.Trim(), StringComparison.OrdinalIgnoreCase));

        if (intent is null)
            return $"Nothing recorded: no intent named '{intentName}'.";

        intent.Description = description?.Trim();

        if (intent.Origin == Provenance.Imported)
            intent.Origin = Provenance.Revised;

        return $"'{intentName}' description revised.";
    }

    /// <summary>
    /// Opens a tool on the named agent or revises the description of one already open.
    /// </summary>
    /// <remarks>
    /// Touches only the tool's name and description. Parameters are a separate concern reached
    /// through <see cref="SetToolParameter"/> — a revision here never disturbs a parameter list
    /// already recorded on the same tool, whether the tool is new or already existed.
    /// </remarks>
    [Description("Opens a tool on the named agent or revises the description of one already open.")]
    public string DeclareTool(
        [Description("The agent's ID.")] string agentId,
        [Description("PascalCase, a verb the agent performs.")] string name,
        [Description("What the tool does, what it returns and when to call it.")] string description)
    {
        if (Find(agentId) is not { } agent)
            return $"No tool recorded: no agent named '{agentId}'.";

        string cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
            return "No tool recorded: a tool must have a name.";

        ToolDraft? existing = FindTool(agent, cleanName);
        bool revision = existing is not null;
        ToolDraft tool = existing ?? new ToolDraft { Name = cleanName, Origin = Provenance.Authored };
        tool.Description = description?.Trim();

        if (!revision)
            agent.Tools.Add(tool);

        MarkRevised(agent);

        string complaint = InterviewTool.IdentifierComplaint(cleanName, "tool name", pascalCase: true);
        return (revision ? $"'{cleanName}' revised on {agentId}." : $"'{cleanName}' declared on {agentId}.")
               + (complaint.Length > 0 ? " " + complaint : string.Empty);
    }

    /// <summary>
    /// Adds a parameter to a declared tool of the named agent or revises one already there.
    /// </summary>
    /// <remarks>
    /// Touches exactly the one parameter named — every other parameter already on the tool and
    /// the tool's own name and description, are left as they stand. The tool itself must already
    /// exist: this never creates one, since a coherence finding names a tool it has already read.
    /// </remarks>
    [Description("Adds a parameter to a declared tool on the named agent or revises one already there by name and in place.")]
    public string SetToolParameter(
        [Description("The agent's ID.")] string agentId,
        [Description("The exact name of a tool already declared on that agent.")] string toolName,
        [Description("camelCase parameter name.")] string name,
        [Description("What the value is and what a good one looks like.")] string description,
        [Description("'request', 'context' or 'none'.")] string scope,
        [Description("Whether the agent must supply this value on every call.")] bool required,
        [Description("Whether the resolved value is published to the whole conversation. Only meaningful alongside scope 'context'.")] bool shared)
    {
        if (Find(agentId) is not { } agent)
            return $"No parameter recorded: no agent named '{agentId}'.";

        if (FindTool(agent, toolName) is not { } tool)
            return $"No parameter recorded: no tool named '{toolName}' on {agentId}.";

        string cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
            return "No parameter recorded: a parameter must have a name.";

        // "none"/"null" are read back as the empty scope, the same value a value the model itself
        // authors (a quick reply, a rich card) legitimately declares — anything else passes through
        // verbatim so an unrecognised scope surfaces as a validation finding rather than being
        // silently coerced into one of the two known ones.
        string cleanScope = (scope ?? string.Empty).Trim().ToLowerInvariant();
        string? resolvedScope = cleanScope switch
        {
            Constants.Scopes.Context => Constants.Scopes.Context,
            Constants.Scopes.Request => Constants.Scopes.Request,
            "" or "none" or "null" => null,
            _ => cleanScope
        };

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

        return $"'{cleanName}' recorded on {agentId}.{tool.Name}.";
    }

    /// <summary>
    /// Removes a parameter from a tool of the named agent.
    /// </summary>
    [Description("Removes a parameter from a tool on the named agent.")]
    public string DropToolParameter(
        [Description("The agent's ID.")] string agentId,
        [Description("The exact name of the tool.")] string toolName,
        [Description("The exact name of the parameter to remove.")] string parameterName)
    {
        if (Find(agentId) is not { } agent || FindTool(agent, toolName) is not { } tool)
            return $"Nothing dropped: no such tool on '{agentId}'.";

        int removed = tool.Parameters.RemoveAll(p =>
            string.Equals(p.Name, parameterName?.Trim(), StringComparison.Ordinal));

        if (removed > 0)
            MarkRevised(agent);

        return removed > 0
            ? $"'{parameterName}' dropped from {agentId}.{tool.Name}."
            : $"Nothing dropped: {tool.Name} has no parameter named '{parameterName}'.";
    }

    /// <summary>
    /// Removes a tool and everything on it from the named agent.
    /// </summary>
    [Description("Removes a tool and every parameter on it from the named agent — for when a duplicated reach is resolved by keeping only one shape.")]
    public string DropTool(
        [Description("The agent's ID.")] string agentId,
        [Description("The exact name of the tool to remove.")] string toolName)
    {
        if (Find(agentId) is not { } agent)
            return $"Nothing dropped: no agent named '{agentId}'.";

        int removed = agent.Tools.RemoveAll(t =>
            string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));

        if (removed > 0)
            MarkRevised(agent);

        return removed > 0
            ? $"'{toolName}' dropped from {agentId}, with its parameters."
            : $"Nothing dropped: {agentId} has no tool named '{toolName}'.";
    }

    /// <summary>
    /// Declares the fix applied.
    /// </summary>
    [Description("Declares the fix applied. Call exactly once, last.")]
    public string ApplyCompleted(
        [Description("One sentence, in the domain's own words, naming the field and the agent changed.")] string summary)
    {
        Summary = string.IsNullOrWhiteSpace(summary) ? "Applied." : summary.Trim();
        return "Recorded.";
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
