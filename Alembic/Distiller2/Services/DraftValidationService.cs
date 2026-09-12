using Distiller2.Interfaces;
using Distiller2.Model;
using Morgana.AI;

namespace Distiller2.Services;

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
    private static readonly string[] KnownScopes = [Constants.Scopes.Context, Constants.Scopes.Request];

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

        Collect(findings, ValidationCheck.Intents, found => ValidateIntents(draft, found));
        Collect(findings, ValidationCheck.Routing, found => ValidateIntentAgentPairing(draft, found));
        Collect(findings, ValidationCheck.Colleagues, found => ValidateConsultations(draft, found));

        // The agent is checked beside the entry that routes to it: what a colleague is published
        // and what the classifier routes on are two sentences about one desk and only readable
        // against each other.
        foreach (AgentDraft agent in draft.Agents)
        {
            List<ValidationFinding> own = [];

            ValidateAgent(agent, draft.Intents.FirstOrDefault(i =>
                string.Equals(i.Name, agent.ID, StringComparison.OrdinalIgnoreCase)), own);

            findings.AddRange(own.Select(finding => finding with { Agent = agent.ID, Check = CheckOf(finding) }));
        }

        // Errors first, then warnings, each group keeping the order the domain declares its
        // elements in: a client reading top-down meets what stops them before what merely
        // deserves a look, without losing the file's own sequence inside each group.
        return [.. findings.OrderByDescending(f => f.Severity)];
    }

    /// <summary>
    /// Runs one family of checks and files what it found under that family and the agent it names.
    /// </summary>
    // Those families speak of one element per finding, quoted in their Where as 'name': the agent
    // an intent reaches carries the intent's own name.
    private static void Collect(List<ValidationFinding> findings, ValidationCheck check, Action<List<ValidationFinding>> validate)
    {
        List<ValidationFinding> found = [];
        validate(found);

        findings.AddRange(found.Select(finding => finding with
        {
            Check = check,
            Agent = QuotedName.Match(finding.Where) is { Success: true } quoted ? quoted.Groups[1].Value : null
        }));
    }

    /// <summary>
    /// The name an intent or agent finding quotes in its Where.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex QuotedName =
        new(@"^(?:intent|agent) '([^']*)'", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Which family an agent's own finding belongs to, read off the Where its check wrote.
    /// </summary>
    // A class name is the code's, a tool or parameter is addressed as agent.tool, everything else is the agent's prose.
    private static ValidationCheck CheckOf(ValidationFinding finding) =>
        finding.Where.EndsWith("class name", StringComparison.Ordinal) ? ValidationCheck.Code
        : finding.Where.StartsWith("agent '", StringComparison.Ordinal) ? ValidationCheck.Agents
        : ValidationCheck.Tools;

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

            if (string.IsNullOrWhiteSpace(intent.Label))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    "The intent has no label.",
                    "The presenter derives its quick-reply buttons from the labels; a missing one costs this intent its button."));
        }
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

        findings.AddRange(
            draft.Intents.Where(i => !string.IsNullOrWhiteSpace(i.Name)
                                       && !agentIds.Contains(i.Name!))
                         .Select(intent => new ValidationFinding(FindingSeverity.Error, $"intent '{intent.Name}'", "No agent handles this intent.", "HandlesIntentAgentRegistryService checks this in both directions at startup and throws on a mismatch.")));

        findings.AddRange(
            draft.Agents.Where(a => !string.IsNullOrWhiteSpace(a.ID)
                                      && !intentNames.Contains(a.ID!))
                        .Select(agent => new ValidationFinding(FindingSeverity.Error, $"agent '{agent.ID}'", "No intent declares this agent.", "An agent nothing routes to is unreachable and the startup registry treats it as a configuration error rather than dead weight.")));
    }

    /// <summary>
    /// Checks every declared colleague against the domain that has to satisfy it.
    /// </summary>
    /// <remarks>
    /// <c>HandlesIntentAgentRegistryService</c> validates the same two things at startup and throws:
    /// a <c>[ConsultsAgent]</c> naming an intent no agent handles and one naming the declaring
    /// agent's own. Here they cost nothing to fix. The third finding is not a startup rule at all
    /// but the shape of the framework's own refusal — a colleague answering a consultation is denied
    /// its own peer functions — so a chain reaches one hop and no further and an author who drew a
    /// chain expecting two is the person this exists to tell.
    /// <para>
    /// None of the three can be said of a colleague at an instance. Its intent is answered by a domain
    /// this one cannot see, its own edges are unknowable and an agent consulting a namesake of its
    /// own at another installation is ordinary rather than circular. So what is checked there is the
    /// only thing this side owns: that the instance was named at all.
    /// </para>
    /// </remarks>
    private static void ValidateConsultations(DomainDraft draft, List<ValidationFinding> findings)
    {
        HashSet<string> handled = new(
            draft.Agents.Where(a => !string.IsNullOrWhiteSpace(a.ID)).Select(a => a.ID!),
            StringComparer.OrdinalIgnoreCase);

        foreach (AgentDraft agent in draft.Agents)
        {
            string where = $"agent '{agent.ID ?? "(unnamed)"}'";

            foreach (Morgana.AI.Records.PeerReference colleague in agent.Code.Consults)
            {
                if (colleague.Instance is not null)
                {
                    if (string.IsNullOrWhiteSpace(colleague.Instance))
                        findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                            $"It consults '{colleague.Intent}' at a system with no name.",
                            "The name is matched against an entry under Morgana:AgentToAgent:Partners and a blank one matches nothing: startup refuses it."));

                    continue;
                }

                if (string.Equals(colleague.Intent, agent.ID, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                        $"It declares itself as a colleague ('{colleague.Intent}').",
                        "The startup registry refuses a [ConsultsAgent] naming the declaring agent's own intent: an agent cannot be its own second opinion."));
                    continue;
                }

                if (!handled.Contains(colleague.Intent))
                {
                    findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                        $"It consults '{colleague.Intent}', which no agent of this domain handles.",
                        "A consultation is resolved when the agent is created, so a name nothing answers is startup-fatal rather than a colleague that quietly never appears."));
                    continue;
                }

                AgentDraft? far = draft.Agents.FirstOrDefault(a => string.Equals(a.ID, colleague.Intent, StringComparison.OrdinalIgnoreCase));

                if (far?.Code.Consults.Count > 0)
                    findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                        $"It consults '{colleague.Intent}', which consults a colleague of its own.",
                        "While it answers a consultation the framework withholds its peer functions, so the second hop never happens: whatever the far agent would have asked for is not in the answer this one gets."));
            }
        }
    }

    /// <summary>One reading of a sentence, with its spacing and punctuation out of the way.</summary>
    private static string Compact(string? text) =>
        new string([.. (text ?? string.Empty).Where(char.IsLetterOrDigit)]).ToLowerInvariant();

    /// <summary>
    /// Checks one agent's prose, its class names and its whole toolkit.
    /// </summary>
    private static void ValidateAgent(AgentDraft agent, IntentDraft? intent, List<ValidationFinding> findings)
    {
        string where = $"agent '{agent.ID ?? "(unnamed)"}'";

        if (string.IsNullOrWhiteSpace(agent.ID))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The agent has no ID.",
                "The ID is what pairs a prompt with its intent and its [HandlesIntent] attribute."));

        if (string.IsNullOrWhiteSpace(agent.Target))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The agent has no Target.",
                "Target is the domain layer's first section and states what the agent is for; composed empty, the agent inherits only the framework's generic purpose.") { Step = InterviewStep.AgentTarget });

        if (string.IsNullOrWhiteSpace(agent.ConsultMeFor))
            findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                "The agent has nothing to say to a colleague consulting it.",
                "ConsultMeFor is what a colleague reads to decide whether a question is this agent's; without it the card falls back to the intent description, which is a routing phrase written for the classifier.") { Step = InterviewStep.AgentTerritory });

        // The card carries one sentence about this desk and a colleague weighing a question reads
        // that and nothing else. Two ways it comes out useless are decidable here: written as the
        // operations the desk performs, which invites a caller to rule its question out, and left as
        // the phrase the classifier routes on, which says which utterances land here rather than
        // what this desk answers for.
        string? territory = AgentRows.Plain(agent.ConsultMeFor);

        if (!string.IsNullOrWhiteSpace(territory))
        {
            string? named = agent.Tools
                .Select(tool => tool.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)
                                        && territory.Contains(name!, StringComparison.OrdinalIgnoreCase));

            if (named is not null)
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    $"What this agent publishes to a colleague names its own tool '{named}'.",
                    "ConsultMeFor states a territory: a colleague handed an inventory of functions rules its question out instead of asking it.") { Step = InterviewStep.AgentTerritory });

            if (intent is not null && string.Equals(Compact(territory), Compact(intent.Description), StringComparison.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    "What this agent publishes to a colleague is its own routing description.",
                    "The intent description tells the classifier which user utterances land here, never what this desk answers for — a caller reading it back learns nothing it could ask about.") { Step = InterviewStep.AgentTerritory });
        }

        if (string.IsNullOrWhiteSpace(agent.Instructions))
            findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                "The agent has no Instructions.",
                "Legal — the framework layer already governs how a turn is formed — but it means this agent adds no domain constraint of its own.") { Step = InterviewStep.AgentInstructions });

        if (string.IsNullOrWhiteSpace(agent.Formatting))
            findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                "The agent has no Formatting.",
                "Formatting is where an agent says how its own tools' output should be presented, which no global policy can know for it.") { Step = InterviewStep.AgentFormatting });

        // Deliberately not a finding, for the same reason as Code.Inferred below: whether zero
        // native tools means "MCP-only, genuinely nothing to declare" or "the author forgot" is
        // undecidable without knowing which MCP servers the agent reaches at runtime and that is a
        // C# fact — AgentCodeFacts.MCPServers — that is nowhere in an agents.json import and no
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
    private static void ValidateTool(AgentDraft agent, ToolDraft tool, HashSet<string> toolNames, List<ValidationFinding> toolkit)
    {
        // Every finding about a tool is rewritten in the Toolkit step.
        ToolkitFindings findings = new(toolkit);

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
                    "The generated method could not declare them and the JSON schema handed to the model would carry one property, not two."));

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
                && !string.Equals(parameter.Scope, Constants.Scopes.Context, StringComparison.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, parameterWhere,
                    "The parameter is Shared but not context-scoped.",
                    "Shared publishes a resolved context variable to the conversation's shared_context registry, so it only means anything alongside Scope 'context'."));

            if (parameter.Required && optionalSeen)
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "A required parameter follows an optional one.",
                    "C# cannot declare that method signature and MorganaToolAdapter.AddTool validates the required/optional split against the delegate."));

            optionalSeen |= !parameter.Required;
        }
    }

    /// <summary>
    /// A finding list that files every tool finding under the Toolkit step as it is added.
    /// </summary>
    private sealed class ToolkitFindings(List<ValidationFinding> findings)
    {
        /// <summary>The agent's own finding list the tool findings land in.</summary>
        private List<ValidationFinding> Items { get; } = findings;

        /// <summary>Adds a finding, marked as the Toolkit step's to rewrite.</summary>
        public void Add(ValidationFinding finding) => Items.Add(finding with { Step = InterviewStep.AgentToolkit });

        /// <summary>The list underneath, for the identifier check shared with the agent's own names.</summary>
        public static implicit operator List<ValidationFinding>(ToolkitFindings toolkit) => toolkit.Items;
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
                $"The {what} becomes C# verbatim and a keyword cannot be used bare as an identifier."));
    }
}
