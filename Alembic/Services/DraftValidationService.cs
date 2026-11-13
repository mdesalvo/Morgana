using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Tools;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IDraftValidationService"/>: the deterministic checks, each restating a rule Morgana enforces later and more expensively.
/// The framework's refusal arrives after the client has packaged, deployed and run; the same sentence here arrives while it costs nothing to change.
/// </summary>
public class DraftValidationService : IDraftValidationService
{
    /// <summary>The base tools every agent receives from <c>MorganaTool</c>, read off the class itself; Morgana refuses a domain tool sharing one of these names in any case.</summary>
    private static readonly HashSet<string> BaseToolNames = new(
        MorganaToolAdapter.GetToolMethods(typeof(MorganaTool)).Select(method => method.Name),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>The scopes a parameter may declare; a parameter carrying a value the model authors declares none, which is why an empty scope is legal.</summary>
    private static readonly string[] KnownScopes = [Constants.Scopes.Context, Constants.Scopes.Request];

    /// <summary>The C# keywords a domain author plausibly reaches for when naming something, none of which can stand bare as an identifier.</summary>
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

    /// <summary>The name an intent or agent finding quotes in its Where.</summary>
    private static readonly System.Text.RegularExpressions.Regex QuotedName =
        new(@"^(?:intent|agent) '([^']*)'", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Runs every deterministic check over the Draft.</summary>
    /// <returns>Findings, errors first, in the order the domain declares its elements.</returns>
    public IReadOnlyList<ValidationFinding> Validate(DomainDraft draft)
    {
        List<ValidationFinding> findings = [];

        // The checks that span the domain: names, the pairing of intents with agents and the colleagues each declares.
        Collect(findings, ValidationCheck.Intents, found => ValidateIntents(draft, found));
        Collect(findings, ValidationCheck.Routing, found => ValidateIntentAgentPairing(draft, found));
        Collect(findings, ValidationCheck.Colleagues, found => ValidateConsultations(draft, found));

        // The agent is checked beside the entry that routes to it: what a colleague is published
        // and what the classifier routes on are two sentences about one agent and only readable
        // against each other.
        foreach (AgentDraft agent in draft.Agents)
        {
            List<ValidationFinding> own = [];

            ValidateAgent(agent, draft.Intents.FirstOrDefault(intent =>
                string.Equals(intent.Name, agent.ID, StringComparison.OrdinalIgnoreCase)), own);

            // Every finding is filed under its agent and the family of check that raised it, which the page groups by.
            findings.AddRange(own.Select(finding => finding with { Agent = agent.ID, Check = CheckOf(finding) }));
        }

        // Errors first, then warnings, each group keeping the order the domain declares its
        // elements in: a client reading top-down meets what stops them before what merely
        // deserves a look, without losing the file's own sequence inside each group.
        return [.. findings.OrderByDescending(finding => finding.Severity)];
    }

    /// <summary>
    /// Runs one family of checks and files what it found under that family and the agent it names.
    /// Those families speak of one element per finding, quoted in its Where; the agent an intent reaches carries the intent's own name.
    /// </summary>
    private static void Collect(List<ValidationFinding> findings, ValidationCheck check, Action<List<ValidationFinding>> validate)
    {
        // The family's findings, gathered before they are filed.
        List<ValidationFinding> found = [];
        validate(found);

        // Filed under the family and the name the Where quotes, so the page can show each beside its agent.
        findings.AddRange(found.Select(finding => finding with
        {
            Check = check,
            Agent = QuotedName.Match(finding.Where) is { Success: true } quoted ? quoted.Groups[1].Value : null
        }));
    }

    /// <summary>
    /// Which family an agent's own finding belongs to, read off the Where its check wrote.
    /// A class name is the code's, a tool or parameter is addressed as agent.tool and everything else is the agent's prose.
    /// </summary>
    private static ValidationCheck CheckOf(ValidationFinding finding) =>
        finding.Where.EndsWith("class name", StringComparison.Ordinal) ? ValidationCheck.Code
        : finding.Where.StartsWith("agent '", StringComparison.Ordinal) ? ValidationCheck.Agents
        : ValidationCheck.Tools;

    /// <summary>Checks every intent for a name, a description, a label and a default value, its name also for duplication or collision with the framework's prompt ids.</summary>
    private static void ValidateIntents(DomainDraft draft, List<ValidationFinding> findings)
    {
        // Names already met, compared as Morgana compares them.
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (IntentDraft intent in draft.Intents)
        {
            string where = $"intent '{intent.Name ?? "(unnamed)"}'";

            // Nothing else about a nameless intent can be judged: everything keys on the name.
            if (string.IsNullOrWhiteSpace(intent.Name))
            {
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    "The intent has no name.",
                    "An intent is addressed by name: it keys the agent prompt, the [HandlesIntent] attribute and the classifier's own vocabulary."));
                continue;
            }

            // A second intent under one name, casing aside, is the duplicate the configuration service refuses.
            if (!seen.Add(intent.Name))
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    "Two intents carry this name.",
                    "Prompt IDs are matched case-insensitively, so two intents differing only in case are one intent to Morgana."));

            // Morgana refuses at startup an intent named after one of her own prompts.
            if (Constants.Prompts.All.Contains(intent.Name, StringComparer.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    $"'{intent.Name}' is one of the framework's own prompt IDs.",
                    "Morgana refuses at startup a domain intent or agent prompt that takes the id of one of the framework's own prompts, since an agent's prompt is looked up by its intent's name in the same registry."));

            // Legal, but the classifier is then left matching on the name alone.
            if (string.IsNullOrWhiteSpace(intent.Description))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    "The intent has no description.",
                    "The description is the only thing the classifier reads about this intent; without one it can only match on the name."));

            // Every intent is a button on Morgana's welcome, so both of a button's parts are required at startup.
            if (string.IsNullOrWhiteSpace(intent.Label))
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    "The intent has no label.",
                    "Morgana refuses to start: every intent is offered as a button and the label is what the button shows."));

            if (string.IsNullOrWhiteSpace(intent.DefaultValue))
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    "The intent has no default value.",
                    "Morgana refuses to start: every intent is offered as a button and the default value is what pressing it sends."));
        }
    }

    /// <summary>
    /// Checks that intents and agents pair up in both directions, the startup registry's own check moved forward to authoring time.
    /// At startup it is an exception thrown at a client who has already packaged and deployed.
    /// </summary>
    private static void ValidateIntentAgentPairing(DomainDraft draft, List<ValidationFinding> findings)
    {
        // Both sides by name, casing aside, as the registry pairs them.
        HashSet<string> agentIds = new(
            draft.Agents.Where(agent => !string.IsNullOrWhiteSpace(agent.ID)).Select(agent => agent.ID!),
            StringComparer.OrdinalIgnoreCase);

        HashSet<string> intentNames = new(
            draft.Intents.Where(intent => !string.IsNullOrWhiteSpace(intent.Name)).Select(intent => intent.Name!),
            StringComparer.OrdinalIgnoreCase);

        // An intent nobody handles: the classifier would route requests to an agent that does not exist.
        findings.AddRange(
            draft.Intents.Where(intentDraft => !string.IsNullOrWhiteSpace(intentDraft.Name)
                                       && !agentIds.Contains(intentDraft.Name!))
                         .Select(intent => new ValidationFinding(FindingSeverity.Error, $"intent '{intent.Name}'", "No agent handles this intent.", "HandlesIntentAgentRegistryService checks this in both directions at startup and throws on a mismatch.")));

        // An agent no intent declares: nothing could ever route to it.
        findings.AddRange(
            draft.Agents.Where(agentDraft => !string.IsNullOrWhiteSpace(agentDraft.ID)
                                      && !intentNames.Contains(agentDraft.ID!))
                        .Select(agent => new ValidationFinding(FindingSeverity.Error, $"agent '{agent.ID}'", "No intent declares this agent.", "An agent nothing routes to is unreachable and the startup registry treats it as a configuration error rather than dead weight.")));
    }

    /// <summary>
    /// Checks every declared colleague against the domain that has to satisfy it, where the startup registry would throw for the first two findings.
    /// The third, a chain longer than one hop, is the framework's refusal of a second hop seen in advance; a colleague at a partner is checked only for a partner name,
    /// since its intent is answered by a domain this one cannot see.
    /// </summary>
    private static void ValidateConsultations(DomainDraft draft, List<ValidationFinding> findings)
    {
        // The intents this domain's own agents answer.
        HashSet<string> handled = new(
            draft.Agents.Where(agent => !string.IsNullOrWhiteSpace(agent.ID)).Select(agent => agent.ID!),
            StringComparer.OrdinalIgnoreCase);

        foreach (AgentDraft agent in draft.Agents)
        {
            string where = $"agent '{agent.ID ?? "(unnamed)"}'";

            foreach (Morgana.AI.Records.PeerReference colleague in agent.Code.Consults)
            {
                // A colleague at a partner: only its name is this side's to get right.
                if (colleague.Instance is not null)
                {
                    // A blank partner name matches no entry under Partners and startup refuses it.
                    if (string.IsNullOrWhiteSpace(colleague.Instance))
                        findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                            $"It consults '{colleague.Intent}' at a system with no name.",
                            "The name is matched against an entry under Morgana:AgentToAgent:Partners and a blank one matches nothing: startup refuses it."));

                    continue;
                }

                // An agent consulting itself is refused at startup.
                if (string.Equals(colleague.Intent, agent.ID, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                        $"It declares itself as a colleague ('{colleague.Intent}').",
                        "The startup registry refuses a [ConsultsAgent] naming the declaring agent's own intent: an agent cannot be its own second opinion."));
                    continue;
                }

                // A colleague no agent of this domain answers is refused at startup too.
                if (!handled.Contains(colleague.Intent))
                {
                    findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                        $"It consults '{colleague.Intent}', which no agent of this domain handles.",
                        "A consultation is resolved when the agent is created, so a name nothing answers is startup-fatal rather than a colleague that quietly never appears."));
                    continue;
                }

                // The colleague consulted, whose own colleagues are out of reach while it answers.
                AgentDraft? far = draft.Agents.FirstOrDefault(candidate => string.Equals(candidate.ID, colleague.Intent, StringComparison.OrdinalIgnoreCase));

                // A chain drawn two hops deep, which the framework cuts at one.
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

    /// <summary>Checks one agent's prose, its class names and its whole toolkit.</summary>
    private static void ValidateAgent(AgentDraft agent, IntentDraft? intent, List<ValidationFinding> findings)
    {
        string where = $"agent '{agent.ID ?? "(unnamed)"}'";

        // Without an id the agent pairs with no intent and no attribute.
        if (string.IsNullOrWhiteSpace(agent.ID))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The agent has no ID.",
                "The ID is what pairs a prompt with its intent and its [HandlesIntent] attribute."));

        // Each missing section is filed under the interview step that writes it, so the page can open that step.
        if (string.IsNullOrWhiteSpace(agent.Target))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The agent has no Target.",
                "Target is the domain layer's first section and states what the agent is for; composed empty, the agent inherits only the framework's generic purpose.") { Step = InterviewStep.AgentTarget });

        if (string.IsNullOrWhiteSpace(agent.Territory))
            findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                "The agent has nothing to say to a colleague consulting it.",
                "Territory is what a colleague reads to decide whether a question is this agent's; without it the card falls back to the intent description, which is a routing phrase written for the classifier.") { Step = InterviewStep.AgentTerritory });

        // The card carries one sentence about this agent and a colleague weighing a question reads
        // that and nothing else. Two ways it comes out useless are decidable here: written as the
        // operations the agent performs, which invites a caller to rule its question out; left as
        // the phrase the classifier routes on, which says which utterances land here rather than
        // what this agent answers for.
        string? territory = AgentRows.Plain(agent.Territory);

        if (!string.IsNullOrWhiteSpace(territory))
        {
            // The first of the agent's own tools the territory names, if it names any.
            string? named = agent.Tools
                .Select(tool => tool.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)
                                        && territory.Contains(name!, StringComparison.OrdinalIgnoreCase));

            // An inventory of functions instead of a territory.
            if (named is not null)
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    $"What this agent publishes to a colleague names its own tool '{named}'.",
                    "Territory states a territory: a colleague handed an inventory of functions rules its question out instead of asking it.") { Step = InterviewStep.AgentTerritory });

            // The classifier's routing phrase recycled, compared without spacing and punctuation.
            if (intent is not null && string.Equals(Compact(territory), Compact(intent.Description), StringComparison.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    "What this agent publishes to a colleague is its own routing description.",
                    "The intent description tells the classifier which user utterances land here, never what this agent answers for — a caller reading it back learns nothing it could ask about.") { Step = InterviewStep.AgentTerritory });
        }

        // Legal, since the framework already governs a turn, but the agent then adds no constraint of its own.
        if (string.IsNullOrWhiteSpace(agent.Instructions))
            findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                "The agent has no Instructions.",
                "Legal — the framework layer already governs how a turn is formed — but it means this agent adds no domain constraint of its own.") { Step = InterviewStep.AgentInstructions });

        // How the agent's own tools' output is shown is something no global policy can know for it.
        if (string.IsNullOrWhiteSpace(agent.Formatting))
            findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                "The agent has no Formatting.",
                "Formatting is where an agent says how its own tools' output should be presented, which no global policy can know for it.") { Step = InterviewStep.AgentFormatting });

        // Not a finding: whether zero native tools means an agent served by MCP or one whose author forgot
        // is undecidable without the MCP servers it reaches, a C# fact no agents.json and no unmorganized save file
        // carries. A warning that fires on both cases alike teaches neither apart.

        // Not a finding either: agent.Code.Inferred is true of every agent that has not been through Morganize yet,
        // imported, resumed or freshly written alike, so a warning would fire on every first Review and tell
        // nothing; Morganize's own panel resolves it where it is actionable.

        // The class names become C# verbatim.
        ValidateIdentifier(agent.Code.AgentClassName, $"{where} class name", "class name", findings);
        ValidateIdentifier(agent.Code.ToolClassName, $"{where} tool class name", "class name", findings);

        // Tool names met so far in this agent, compared as C# compares method names.
        HashSet<string> toolNames = new(StringComparer.Ordinal);

        foreach (ToolDraft tool in agent.Tools)
            ValidateTool(agent, tool, toolNames, findings);
    }

    /// <summary>Checks one tool's name, description and parameter list.</summary>
    private static void ValidateTool(AgentDraft agent, ToolDraft tool, HashSet<string> toolNames, List<ValidationFinding> toolkit)
    {
        // Every finding about a tool is rewritten in the Toolkit step.
        ToolkitFindings findings = new(toolkit);

        string where = $"{agent.ID}.{tool.Name ?? "(unnamed)"}";

        // A nameless tool has no method to check anything else on.
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The tool has no name.",
                "A tool is addressed by its C# method name: it is the name the model calls."));
            return;
        }

        // Two methods under one name in one tool class.
        if (!toolNames.Add(tool.Name))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "This agent declares two tools with this name.",
                "Tools are registered by name per agent, so the second registration replaces or collides with the first."));

        // A base tool's name, which the startup checks refuse in any casing.
        if (BaseToolNames.Contains(tool.Name))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                $"'{tool.Name}' is one of the base tools every agent already receives.",
                "MorganaTool gives every agent GetContextVariable, SetContextVariable, SetTurnEnding, SetQuickReplies and SetRichCard; a domain tool cannot share a name with one."));

        // The name becomes a C# method verbatim.
        ValidateIdentifier(tool.Name, where, "tool name", findings);

        // The startup checks refuse a tool without [Description].
        if (string.IsNullOrWhiteSpace(tool.Description))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The tool has no description.",
                "The description is what the model reads when it decides whether to call this tool at all; an empty one leaves the decision to the name."));

        // Parameter names met so far and whether an optional one has come, which no required one may follow.
        HashSet<string> parameterNames = new(StringComparer.Ordinal);
        bool optionalSeen = false;

        foreach (ToolParameterDraft parameter in tool.Parameters)
        {
            string parameterWhere = $"{where}.{parameter.Name ?? "(unnamed)"}";

            // A nameless parameter has no C# declaration to check anything else on.
            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "The parameter has no name.",
                    "A parameter is addressed by its C# parameter name: it is the name the model passes the value under."));
                continue;
            }

            // Two parameters under one name cannot both be declared.
            if (!parameterNames.Add(parameter.Name))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "This tool declares two parameters with this name.",
                    "The generated method could not declare them and the JSON schema handed to the model would carry one property, not two."));

            // The name becomes a C# parameter verbatim.
            ValidateIdentifier(parameter.Name, parameterWhere, "parameter name", findings);

            // The startup checks refuse a parameter without [Description].
            if (string.IsNullOrWhiteSpace(parameter.Description))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "The parameter has no description.",
                    "Morgana refuses at startup a tool parameter without [Description]: it is what the model reads to know what to pass."));

            // A scope that is neither context nor request has no attribute to be emitted as.
            if (!string.IsNullOrWhiteSpace(parameter.Scope)
                && !KnownScopes.Contains(parameter.Scope, StringComparer.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    $"'{parameter.Scope}' is not a scope.",
                    "A parameter resolving an input declares 'context' or 'request'; one carrying a value the model itself authors declares none."));

            // Sharing applies to a context value alone.
            if (parameter.Shared
                && !string.Equals(parameter.Scope, Constants.Scopes.Context, StringComparison.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "The parameter is Shared but not context-scoped.",
                    "Only a context value is shared: it is emitted as [ToolParameter(Records.ToolScope.SharedContext)], which a request or authored value cannot be."));

            // C# allows no required parameter after an optional one.
            if (parameter.Required && optionalSeen)
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "A required parameter follows an optional one.",
                    "C# cannot declare that method signature."));

            // Once one parameter is optional, every later required one is out of order.
            optionalSeen |= !parameter.Required;
        }
    }

    /// <summary>A finding list that files every tool finding under the Toolkit step as it is added.</summary>
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
    /// Checks that a name can be a C# identifier at all, by shape and keywords only.
    /// It catches the names a domain author actually writes, a space, a hyphen, a leading digit or a word like <c>class</c>, while they can still be changed for free.
    /// </summary>
    private static void ValidateIdentifier(string? name, string where, string what, List<ValidationFinding> findings)
    {
        // A missing name has its own finding, raised by the caller.
        if (string.IsNullOrWhiteSpace(name))
            return;

        // A letter or underscore first, then letters, digits and underscores only.
        bool shapeOk = (char.IsLetter(name[0]) || name[0] == '_')
                       && name.All(character => char.IsLetterOrDigit(character) || character == '_');

        // A malformed name cannot be emitted as written, nor can a keyword.
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