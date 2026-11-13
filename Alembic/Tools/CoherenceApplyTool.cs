using System.ComponentModel;
using Alembic.Models;
using Morgana.AI;

namespace Alembic.Tools;

/// <summary>
/// The tools Alembic calls while applying one coherence finding, writing into the agents already in the Draft that the finding names.
/// The finding's own Fix is the instruction, accepted by the client's press, so there is no dialogue; there is no tool for a voice
/// or for the map's entries, since no coherence defect is about either and only an intent's description may be sharpened.
/// </summary>
public class CoherenceApplyTool
{
    /// <summary>The domain the finding was read against, written in place.</summary>
    private readonly DomainDraft draft;

    /// <summary>What changed, in the client's own words, recorded by <see cref="ApplyCompleted"/>.</summary>
    public string? Summary { get; private set; }

    /// <summary>Binds the toolset to the domain the finding was read against.</summary>
    public CoherenceApplyTool(DomainDraft draft)
    {
        this.draft = draft;
    }

    /// <summary>
    /// Returns one agent's prose and toolkit as they stand, by ID.
    /// The pass opens knowing only the finding's words, so it reads the whole agent before a write that could discard what a sibling field depended on.
    /// </summary>
    [Description("Returns one agent's prose and toolkit as they stand, by its ID. Call it for every agent the finding names before changing anything.")]
    public string GetAgent(
        [Description("The agent's ID, exactly as the finding's Where names it.")] string agentId)
    {
        // A name the finding did not use is corrected against the finding's own Where.
        if (Find(agentId) is not { } agent)
            return $"No agent named '{agentId}' in this domain. The finding's Where names agents by their intent ID.";

        // The four sections in the order the composer lays them out, an unwritten one said so.
        List<string> sections =
        [
            agent.Target ?? "(no target)",
            agent.Personality ?? "(no personality)",
            agent.Instructions ?? "(no instructions)",
            agent.Formatting ?? "(no formatting)"
        ];

        // The toolkit with each parameter's scope, optionality and sharing, which a fix about a tool has to keep intact.
        string tools = agent.Tools.Count == 0
            ? "Declares no native tools."
            : "Tools:\n" + string.Join("\n", agent.Tools.Select(t =>
                $"- {t.Name}: {t.Description}"
                + string.Concat(t.Parameters.Select(p =>
                    $"\n    {p.Name} [{p.Scope ?? "authored"}{(p.Required ? "" : ", optional")}{(p.Shared ? ", shared" : "")}]: {p.Description}"))));

        // The agent whole, prose first.
        return string.Join("\n\n", sections) + "\n\n" + tools;
    }

    /// <summary>Records an agent's Target section, replacing it whole.</summary>
    [Description("Replaces the named agent's whole Target section.")]
    public string SetAgentTarget(
        [Description("The agent's ID.")] string agentId,
        [Description("The complete revised Target, addressed to the agent as 'you'. No section label; that is added for you.")] string target)
    {
        // A name the finding did not use writes nothing.
        if (Find(agentId) is not { } agent)
            return $"Nothing recorded: no agent named '{agentId}'.";

        // The section replaced and the agent marked, so the migration report says a coherence fix reached it.
        agent.Target = target?.Trim();
        MarkRevised(agent);
        return $"{agentId}'s Target revised.";
    }

    /// <summary>Records an agent's Instructions section, replacing it whole.</summary>
    [Description("Replaces the named agent's whole Instructions section.")]
    public string SetAgentInstructions(
        [Description("The agent's ID.")] string agentId,
        [Description("The complete revised Instructions, addressed to the agent as 'you'. No section label; that is added for you.")] string instructions)
    {
        // A name the finding did not use writes nothing.
        if (Find(agentId) is not { } agent)
            return $"Nothing recorded: no agent named '{agentId}'.";

        // The section replaced and the agent marked, so the migration report says a coherence fix reached it.
        agent.Instructions = instructions?.Trim();
        MarkRevised(agent);
        return $"{agentId}'s Instructions revised.";
    }

    /// <summary>Records an agent's Formatting section, replacing it whole.</summary>
    [Description("Replaces the named agent's whole Formatting section.")]
    public string SetAgentFormatting(
        [Description("The agent's ID.")] string agentId,
        [Description("The complete revised Formatting, addressed to the agent as 'you'. No section label; that is added for you.")] string formatting)
    {
        // A name the finding did not use writes nothing.
        if (Find(agentId) is not { } agent)
            return $"Nothing recorded: no agent named '{agentId}'.";

        // The section replaced and the agent marked, so the migration report says a coherence fix reached it.
        agent.Formatting = formatting?.Trim();
        MarkRevised(agent);
        return $"{agentId}'s Formatting revised.";
    }

    /// <summary>
    /// Sharpens an intent's description, the one intent-level edit a coherence fix ever makes.
    /// An overlap between intents is resolved by telling two descriptions apart, never by adding, dropping or renaming an entry.
    /// </summary>
    [Description("Sharpens one intent's description — the only edit a coherence fix ever makes to the map itself.")]
    public string SetIntentDescription(
        [Description("The intent's name, exactly as it appears in the domain.")] string intentName,
        [Description("The complete revised description, the classifier's whole basis for routing here.")] string description)
    {
        // Matched ignoring case, as the finding names intents.
        IntentDraft? intent = draft.Intents.FirstOrDefault(i =>
            string.Equals(i.Name, intentName?.Trim(), StringComparison.OrdinalIgnoreCase));

        // An intent the map does not hold writes nothing.
        if (intent is null)
            return $"Nothing recorded: no intent named '{intentName}'.";

        // The routing sentence replaced.
        intent.Description = description?.Trim();

        // An imported intent becomes revised, so the migration report shows the change; one Alembic authored stays authored.
        if (intent.Origin == Provenance.Imported)
            intent.Origin = Provenance.Revised;

        return $"'{intentName}' description revised.";
    }

    /// <summary>
    /// Opens a tool on the named agent or revises the description of one already open, never its parameters.
    /// Parameters are reached through <see cref="SetToolParameter"/>, so a revision here never disturbs a list already recorded.
    /// </summary>
    [Description("Opens a tool on the named agent or revises the description of one already open.")]
    public string DeclareTool(
        [Description("The agent's ID.")] string agentId,
        [Description("PascalCase, a verb the agent performs.")] string name,
        [Description("What the tool does, what it returns and when to call it.")] string description)
    {
        // A name the finding did not use writes nothing.
        if (Find(agentId) is not { } agent)
            return $"No tool recorded: no agent named '{agentId}'.";

        // A tool becomes a C# method, which cannot be nameless.
        string cleanName = (name ?? string.Empty).Trim();
        if (cleanName.Length == 0)
            return "No tool recorded: a tool must have a name.";

        // The tool already open under that name or a new one Alembic authors.
        ToolDraft? existing = FindTool(agent, cleanName);
        bool revision = existing is not null;
        ToolDraft tool = existing ?? new ToolDraft { Name = cleanName, Origin = Provenance.Authored };
        tool.Description = description?.Trim();

        // Only a new tool joins the toolkit; a revised one is already in it.
        if (!revision)
            agent.Tools.Add(tool);

        MarkRevised(agent);

        // The name becomes a method of the generated C#, so a shape the compiler would refuse is reported now.
        string complaint = InterviewTool.IdentifierComplaint(cleanName, "tool name", pascalCase: true);
        return (revision ? $"'{cleanName}' revised on {agentId}." : $"'{cleanName}' declared on {agentId}.")
               + (complaint.Length > 0 ? " " + complaint : string.Empty);
    }

    /// <summary>
    /// Adds a parameter to a declared tool of the named agent or revises one already there, touching nothing else on the tool.
    /// It never creates the tool, since a coherence finding names a tool it has already read.
    /// </summary>
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
        // A name the finding did not use writes nothing.
        if (Find(agentId) is not { } agent)
            return $"No parameter recorded: no agent named '{agentId}'.";

        // The tool must already be on the agent: a fix does not invent one through its parameters.
        if (FindTool(agent, toolName) is not { } tool)
            return $"No parameter recorded: no tool named '{toolName}' on {agentId}.";

        // A parameter becomes a C# parameter, which cannot be nameless.
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
            // The two scopes the framework reads, in their canonical spelling.
            Constants.Scopes.Context => Constants.Scopes.Context,
            Constants.Scopes.Request => Constants.Scopes.Request,

            // A value the model itself authors.
            "" or "none" or "null" => null,

            // Anything else, kept for validation to report.
            _ => cleanScope
        };

        // The parameter already on the tool under that name, matched with the case the C# keeps.
        ToolParameterDraft? existing = tool.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, cleanName, StringComparison.Ordinal));

        // Revised in place or added, every field set from this call alone.
        bool revision = existing is not null;
        ToolParameterDraft parameter = existing ?? new ToolParameterDraft { Name = cleanName };

        parameter.Description = description?.Trim();
        parameter.Scope = resolvedScope;
        parameter.Required = required;
        parameter.Shared = shared;

        // Only a new parameter joins the signature; a revised one keeps its place in it.
        if (!revision)
            tool.Parameters.Add(parameter);

        MarkRevised(agent);

        // The model learns where the parameter now stands.
        return $"'{cleanName}' recorded on {agentId}.{tool.Name}.";
    }

    /// <summary>Removes a parameter from a tool of the named agent.</summary>
    [Description("Removes a parameter from a tool on the named agent.")]
    public string DropToolParameter(
        [Description("The agent's ID.")] string agentId,
        [Description("The exact name of the tool.")] string toolName,
        [Description("The exact name of the parameter to remove.")] string parameterName)
    {
        // A tool the finding did not name drops nothing.
        if (Find(agentId) is not { } agent || FindTool(agent, toolName) is not { } tool)
            return $"Nothing dropped: no such tool on '{agentId}'.";

        // Matched with the case the C# keeps, so a parameter differing only in case is not taken for this one.
        int removed = tool.Parameters.RemoveAll(p =>
            string.Equals(p.Name, parameterName?.Trim(), StringComparison.Ordinal));

        // Only an agent actually changed is marked revised.
        if (removed > 0)
            MarkRevised(agent);

        // A drop that found nothing is said plainly, so the model does not believe the signature changed.
        return removed > 0
            ? $"'{parameterName}' dropped from {agentId}.{tool.Name}."
            : $"Nothing dropped: {tool.Name} has no parameter named '{parameterName}'.";
    }

    /// <summary>Removes a tool and everything on it from the named agent.</summary>
    [Description("Removes a tool and every parameter on it from the named agent — for when a duplicated reach is resolved by keeping only one shape.")]
    public string DropTool(
        [Description("The agent's ID.")] string agentId,
        [Description("The exact name of the tool to remove.")] string toolName)
    {
        // A name the finding did not use drops nothing.
        if (Find(agentId) is not { } agent)
            return $"Nothing dropped: no agent named '{agentId}'.";

        // Matched with the case the C# keeps.
        int removed = agent.Tools.RemoveAll(t =>
            string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));

        // Only an agent actually changed is marked revised.
        if (removed > 0)
            MarkRevised(agent);

        // A drop that found nothing is said plainly, so the model does not believe the toolkit changed.
        return removed > 0
            ? $"'{toolName}' dropped from {agentId}, with its parameters."
            : $"Nothing dropped: {agentId} has no tool named '{toolName}'.";
    }

    /// <summary>Declares the fix applied, with the summary the client reads of what changed.</summary>
    [Description("Declares the fix applied. Call exactly once, last.")]
    public string ApplyCompleted(
        [Description("One sentence, in the domain's own words, naming the field and the agent changed.")] string summary)
    {
        // A fix applied without a summary still tells the client it ran.
        Summary = string.IsNullOrWhiteSpace(summary) ? "Applied." : summary.Trim();
        return "Recorded.";
    }

    /// <summary>Flags an imported agent this pass touched, so the migration report says honestly which of them a coherence fix reached.</summary>
    private static void MarkRevised(AgentDraft agent)
    {
        // An agent Alembic authored stays authored: revised says the client's own agent was changed.
        if (agent.Origin == Provenance.Imported)
            agent.Origin = Provenance.Revised;
    }

    /// <summary>Looks up an agent already in the Draft by its intent ID, ignoring case as the finding's Where names it.</summary>
    private AgentDraft? Find(string? agentId) =>
        draft.Agents.FirstOrDefault(a => string.Equals(a.ID, agentId?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Looks up one of an agent's declared tools by name, with the ordinal comparison validation holds tool names to.</summary>
    private static ToolDraft? FindTool(AgentDraft agent, string? toolName) =>
        agent.Tools.FirstOrDefault(t => string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));
}
