using Alembic.Interfaces;
using Alembic.Model;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IDraftValidationService"/>: the deterministic checks, each one restating a
/// rule Morgana already enforces somewhere later and more expensively.
/// </summary>
public class DraftValidationService : IDraftValidationService
{
    /// <summary>
    /// The framework's own prompt IDs. A domain intent named like one of these makes
    /// <c>ConfigurationPromptResolverService.ResolveAsync</c> throw on a <c>SingleOrDefault</c>
    /// over two matches — deliberately loud, because the alternative is one of the two prompts
    /// becoming permanently unreachable in silence.
    /// </summary>
    private static readonly string[] FrameworkPromptIds =
        ["Morgana", "Classifier", "Guard", "Presentation", "ChannelAdapter"];

    /// <summary>
    /// The base tools every agent receives from <c>morgana.json</c>. A domain tool sharing one of
    /// these names would be registered twice against the same agent.
    /// </summary>
    private static readonly string[] BaseToolNames =
        ["GetContextVariable", "SetContextVariable", "SetTurnContinuation", "SetQuickReplies", "SetRichCard"];

    /// <summary>
    /// The scopes a parameter may declare. A parameter carrying a value the model itself authors
    /// (quick replies, a rich card, a turn continuation) declares none at all, which is why the
    /// empty scope is legal rather than a third value.
    /// </summary>
    private static readonly string[] KnownScopes = ["context", "request"];

    /// <summary>
    /// The intent the framework reserves for "none of the above". It is the one intent exempt from
    /// needing an agent — <c>HandlesIntentAgentRegistryService</c> excludes it by name.
    /// </summary>
    private const string OtherIntent = "other";

    /// <summary>
    /// C# keywords that cannot be used bare as an identifier. Not the full list: only the ones a
    /// domain author plausibly reaches for when naming a tool parameter.
    /// </summary>
    private static readonly HashSet<string> ReservedWords = new(StringComparer.Ordinal)
    {
        "abstract", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
        "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum",
        "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach",
        "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock", "long",
        "namespace", "new", "null", "object", "operator", "out", "override", "params", "private",
        "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try",
        "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void",
        "volatile", "while"
    };

    /// <inheritdoc />
    public IReadOnlyList<ValidationFinding> Validate(DomainDraft draft)
    {
        List<ValidationFinding> findings = [];

        ValidateIntents(draft, findings);
        ValidateIntentAgentPairing(draft, findings);

        foreach (AgentDraft agent in draft.Agents)
            ValidateAgent(agent, findings);

        // Errors first, then warnings, each group keeping the order the domain declares its
        // elements in: a client reading top-down meets what stops them before what merely
        // deserves a look, without losing the file's own sequence inside each group.
        return [.. findings.OrderByDescending(f => f.Severity)];
    }

    /// <summary>
    /// Checks intent names for emptiness, duplication and collision with the framework's own
    /// prompt IDs.
    /// </summary>
    private static void ValidateIntents(DomainDraft draft, List<ValidationFinding> findings)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (IntentDraft intent in draft.Intents)
        {
            string where = $"intent '{intent.Name ?? "(unnamed)"}'";

            if (string.IsNullOrWhiteSpace(intent.Name))
            {
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    "The intent has no name.",
                    "An intent is addressed by name: it keys the agent prompt, the [HandlesIntent] attribute and the classifier's own vocabulary."));
                continue;
            }

            if (!seen.Add(intent.Name))
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    "Two intents carry this name.",
                    "Prompt IDs are matched case-insensitively, so two intents differing only in case are one intent to Morgana."));

            if (FrameworkPromptIds.Contains(intent.Name, StringComparer.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    $"'{intent.Name}' is one of the framework's own prompt IDs.",
                    "ConfigurationPromptResolverService resolves prompts with SingleOrDefault across both layers: a domain ID colliding with a framework one throws at resolution rather than silently shadowing it."));

            if (string.IsNullOrWhiteSpace(intent.Description))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    "The intent has no description.",
                    "The description is the only thing the classifier reads about this intent; without one it can only match on the name."));

            // 'other' is exempt: it is the catch-all, it gets no presenter button, and the shipped
            // Examples domain declares its Label as an explicit null for exactly that reason.
            // Warning about it would train the client to ignore this whole pass.
            if (string.IsNullOrWhiteSpace(intent.Label)
                && !string.Equals(intent.Name, OtherIntent, StringComparison.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    "The intent has no label.",
                    "The presenter derives its quick-reply buttons from the labels; a missing one costs this intent its button."));
        }

        if (!seen.Contains(OtherIntent))
            findings.Add(new ValidationFinding(FindingSeverity.Warning, "domain",
                $"There is no '{OtherIntent}' intent.",
                "The classifier falls back to 'other' whenever it cannot place a message, and that fallback is where a domain decides what happens to everything it does not cover."));
    }

    /// <summary>
    /// Checks that intents and agents pair up in both directions.
    /// </summary>
    /// <remarks>
    /// This is <c>HandlesIntentAgentRegistryService</c>'s bidirectional check, moved forward from
    /// startup to authoring time. There it is an <c>InvalidOperationException</c> thrown at a
    /// client who has already packaged and deployed.
    /// </remarks>
    private static void ValidateIntentAgentPairing(DomainDraft draft, List<ValidationFinding> findings)
    {
        HashSet<string> agentIds = new(
            draft.Agents.Where(a => !string.IsNullOrWhiteSpace(a.ID)).Select(a => a.ID!),
            StringComparer.OrdinalIgnoreCase);

        HashSet<string> intentNames = new(
            draft.Intents.Where(i => !string.IsNullOrWhiteSpace(i.Name)).Select(i => i.Name!),
            StringComparer.OrdinalIgnoreCase);

        foreach (IntentDraft intent in draft.Intents.Where(i =>
                     !string.IsNullOrWhiteSpace(i.Name)
                     && !string.Equals(i.Name, OtherIntent, StringComparison.OrdinalIgnoreCase)
                     && !agentIds.Contains(i.Name!)))
        {
            findings.Add(new ValidationFinding(FindingSeverity.Error, $"intent '{intent.Name}'",
                "No agent handles this intent.",
                "HandlesIntentAgentRegistryService checks this in both directions at startup and throws on a mismatch; 'other' is the single exemption."));
        }

        foreach (AgentDraft agent in draft.Agents.Where(a =>
                     !string.IsNullOrWhiteSpace(a.ID) && !intentNames.Contains(a.ID!)))
        {
            findings.Add(new ValidationFinding(FindingSeverity.Error, $"agent '{agent.ID}'",
                "No intent declares this agent.",
                "An agent nothing routes to is unreachable, and the startup registry treats it as a configuration error rather than dead weight."));
        }
    }

    /// <summary>
    /// Checks one agent's prose, its class names and its whole toolkit.
    /// </summary>
    private static void ValidateAgent(AgentDraft agent, List<ValidationFinding> findings)
    {
        string where = $"agent '{agent.ID ?? "(unnamed)"}'";

        if (string.IsNullOrWhiteSpace(agent.ID))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The agent has no ID.",
                "The ID is what pairs a prompt with its intent and its [HandlesIntent] attribute."));

        if (string.IsNullOrWhiteSpace(agent.Target))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The agent has no Target.",
                "Target is the domain layer's first section and states what the agent is for; composed empty, the agent inherits only the framework's generic purpose."));

        if (string.IsNullOrWhiteSpace(agent.Instructions))
            findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                "The agent has no Instructions.",
                "Legal — the framework layer already governs how a turn is formed — but it means this agent adds no domain constraint of its own."));

        if (string.IsNullOrWhiteSpace(agent.Formatting))
            findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                "The agent has no Formatting.",
                "Formatting is where an agent says how its own tools' output should be presented, which no global policy can know for it."));

        // Deliberately not a finding, for the same reason as Code.Inferred below: whether zero
        // native tools means "MCP-only, genuinely nothing to declare" or "the author forgot" is
        // undecidable without knowing which MCP servers the agent reaches at runtime, and that is a
        // C# fact — AgentCodeFacts.MCPServers — that is nowhere in an agents.json import, and no
        // more decided in an alembic-draft.json save file that has not been through Morganize yet
        // either. A warning that fires identically on both cases teaches neither apart.

        // Deliberately not a finding: agent.Code.Inferred is true of every agent that has not yet
        // visited Morganize — imported from agents.json, resumed from an alembic-draft.json saved
        // before that page, or freshly authored this minute, with no exception among the three — so
        // a warning here would fire on the very first Review after any of them, teaching nothing
        // ("of course I don't have a die, I haven't been to Morganize") and never once discriminating
        // a domain that needs attention from one that does not. Morganize's own panel already
        // surfaces and resolves this directly, where it is actionable rather than tautological.

        ValidateIdentifier(agent.Code.AgentClassName, $"{where} class name", "class name", findings);
        ValidateIdentifier(agent.Code.ToolClassName, $"{where} tool class name", "class name", findings);

        HashSet<string> toolNames = new(StringComparer.Ordinal);

        foreach (ToolDraft tool in agent.Tools)
            ValidateTool(agent, tool, toolNames, findings);
    }

    /// <summary>
    /// Checks one tool's name, description and parameter list.
    /// </summary>
    private static void ValidateTool(AgentDraft agent, ToolDraft tool, HashSet<string> toolNames, List<ValidationFinding> findings)
    {
        string where = $"{agent.ID}.{tool.Name ?? "(unnamed)"}";

        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The tool has no name.",
                "A tool's name must match its C# method name exactly; MorganaToolAdapter.AddTool pairs them by it."));
            return;
        }

        if (!toolNames.Add(tool.Name))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "This agent declares two tools with this name.",
                "Tools are registered by name per agent, so the second registration replaces or collides with the first."));

        if (BaseToolNames.Contains(tool.Name, StringComparer.Ordinal))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                $"'{tool.Name}' is one of the base tools every agent already receives.",
                "morgana.json gives every agent GetContextVariable, SetContextVariable, SetTurnContinuation, SetQuickReplies and SetRichCard; a domain tool cannot share a name with one."));

        ValidateIdentifier(tool.Name, where, "tool name", findings);

        if (string.IsNullOrWhiteSpace(tool.Description))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The tool has no description.",
                "The description is what the model reads when it decides whether to call this tool at all; an empty one leaves the decision to the name."));

        HashSet<string> parameterNames = new(StringComparer.Ordinal);
        bool optionalSeen = false;

        foreach (ToolParameterDraft parameter in tool.Parameters)
        {
            string parameterWhere = $"{where}.{parameter.Name ?? "(unnamed)"}";

            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "The parameter has no name.",
                    "A parameter's name must match the C# method's parameter name; the adapter validates the pair by name, not by position."));
                continue;
            }

            if (!parameterNames.Add(parameter.Name))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "This tool declares two parameters with this name.",
                    "The generated method could not declare them, and the JSON schema handed to the model would carry one property, not two."));

            ValidateIdentifier(parameter.Name, parameterWhere, "parameter name", findings);

            if (string.IsNullOrWhiteSpace(parameter.Description))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, parameterWhere,
                    "The parameter has no description.",
                    "Parameter descriptions reach the model through the JSON schema and nowhere else; an undescribed parameter is emitted bare."));

            if (!string.IsNullOrWhiteSpace(parameter.Scope)
                && !KnownScopes.Contains(parameter.Scope, StringComparer.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    $"'{parameter.Scope}' is not a scope.",
                    "A parameter resolving an input declares 'context' or 'request'; one carrying a value the model itself authors declares none."));

            if (parameter.Shared
                && !string.Equals(parameter.Scope, "context", StringComparison.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, parameterWhere,
                    "The parameter is Shared but not context-scoped.",
                    "Shared publishes a resolved context variable to the conversation's shared_context registry, so it only means anything alongside Scope 'context'."));

            if (parameter.Required && optionalSeen)
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "A required parameter follows an optional one.",
                    "C# cannot declare that method signature, and MorganaToolAdapter.AddTool validates the required/optional split against the delegate."));

            optionalSeen |= !parameter.Required;
        }
    }

    /// <summary>
    /// Checks that a name can be a C# identifier at all.
    /// </summary>
    /// <remarks>
    /// Shape and keywords only. The point is not to reimplement the compiler but to catch the
    /// names a domain author actually writes — a space, a hyphen, a leading digit, or a word like
    /// <c>class</c> — while they can still be changed for free.
    /// </remarks>
    private static void ValidateIdentifier(string? name, string where, string what, List<ValidationFinding> findings)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;

        bool shapeOk = (char.IsLetter(name[0]) || name[0] == '_')
                       && name.All(c => char.IsLetterOrDigit(c) || c == '_');

        if (!shapeOk)
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                $"'{name}' cannot be a C# identifier.",
                $"The {what} becomes C# verbatim: it must start with a letter or underscore and carry only letters, digits and underscores."));
        else if (ReservedWords.Contains(name))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                $"'{name}' is a C# keyword.",
                $"The {what} becomes C# verbatim, and a keyword cannot be used bare as an identifier."));
    }
}
