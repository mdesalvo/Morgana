using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Model;
using Morgana.AI;
using Morgana.AI.Services;

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
    /// The scopes a parameter may declare: every parameter declares one of the two.
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
        // and what the classifier routes on are two sentences about one agent and only readable
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
            string? named = agent.Tools
                .Select(tool => tool.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)
                                        && territory.Contains(name!, StringComparison.OrdinalIgnoreCase));

            if (named is not null)
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    $"What this agent publishes to a colleague names its own tool '{named}'.",
                    "Territory states a territory: a colleague handed an inventory of functions rules its question out instead of asking it.") { Step = InterviewStep.AgentTerritory });

            if (intent is not null && string.Equals(Compact(territory), Compact(intent.Description), StringComparison.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    "What this agent publishes to a colleague is its own routing description.",
                    "The intent description tells the classifier which user utterances land here, never what this agent answers for — a caller reading it back learns nothing it could ask about.") { Step = InterviewStep.AgentTerritory });
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

        // Reply is every agent's and each workflow of this agent has a launcher: a domain tool sharing one of
        // these names would be offered twice to the same agent.
        HashSet<string> reservedToolNames =
        [
            Constants.Tools.Reply,
            .. agent.Workflows
                .Where(workflow => !string.IsNullOrWhiteSpace(workflow.Name))
                .Select(workflow => Constants.Workflows.LauncherPrefix + workflow.Name)
        ];

        foreach (ToolDraft tool in agent.Tools)
            ValidateTool(agent, tool, toolNames, reservedToolNames, findings);

        ValidateWorkflows(agent, where, findings);
    }

    /// <summary>
    /// Replays the framework's startup check of an agent's workflows over what the interview holds.
    /// </summary>
    /// <remarks>
    /// The check itself is the framework's own, so the two can never disagree about what a workflow
    /// may be. A workflow or a step without a name is not handed to it: projected, the missing name
    /// becomes an empty one and the framework's message would speak of a step it cannot name.
    /// </remarks>
    private static void ValidateWorkflows(AgentDraft agent, string where, List<ValidationFinding> findings)
    {
        List<WorkflowDraft> projectable = [];

        foreach (WorkflowDraft workflow in agent.Workflows)
        {
            if (string.IsNullOrWhiteSpace(workflow.Name) || workflow.Steps.Any(step => string.IsNullOrWhiteSpace(step.Name)))
            {
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    $"A workflow of this agent is too incomplete to check: {(string.IsNullOrWhiteSpace(workflow.Name) ? "it has no name" : $"'{workflow.Name}' has a step with no name")}.",
                    "Morgana refuses a workflow or a step without a name at startup: the model starts a workflow by its name and every link between steps is made by theirs.") { Step = InterviewStep.AgentWorkflows });
                continue;
            }

            // The emitted class declares a field per step and a property per carried value, so one name for both does not compile.
            HashSet<string> carriedProperties = [.. DraftProjection.CarriedNames(workflow).Select(CodeEmitService.PropertyName)];
            foreach (Records.WorkflowStep step in workflow.Steps.Where(step => carriedProperties.Contains(step.Name)))
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    $"Workflow '{workflow.Name}' has a step '{step.Name}' named like a value that one of its edges carries.",
                    "The workflow's class would declare the step and the carried value under one name and would not compile.") { Step = InterviewStep.AgentWorkflows });

            projectable.Add(workflow);
        }

        IEnumerable<string> refusals = HandlesIntentAgentRegistryService.ValidateWorkflows(
            agent.ID ?? string.Empty,
            [.. projectable.Select(DraftProjection.ToWorkflowDefinition)],
            agent.Tools.Select(DraftProjection.ToToolDefinition),
            agent.Code.MCPServers.Count > 0);

        foreach (string refusal in refusals)
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                refusal,
                "Morgana refuses it at startup: workflows are checked against the agent's tools and against one another before the first conversation.") { Step = InterviewStep.AgentWorkflows });
    }

    /// <summary>
    /// Checks one tool's name, description and parameter list.
    /// </summary>
    private static void ValidateTool(AgentDraft agent, ToolDraft tool, HashSet<string> toolNames, IReadOnlySet<string> reservedToolNames, List<ValidationFinding> toolkit)
    {
        // Every finding about a tool is rewritten in the Toolkit step.
        ToolkitFindings findings = new(toolkit);

        string where = $"{agent.ID}.{tool.Name ?? "(unnamed)"}";

        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The tool has no name.",
                "A tool's name is its C# method name exactly: the model calls the tool by it."));
            return;
        }

        if (!toolNames.Add(tool.Name))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "This agent declares two tools with this name.",
                "Tools are registered by name per agent, so the second registration replaces or collides with the first."));

        if (reservedToolNames.Contains(tool.Name))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                $"'{tool.Name}' is a function that this agent already receives.",
                $"Every agent receives {Constants.Tools.Reply} and a function named {Constants.Workflows.LauncherPrefix} followed by the name of each of its workflows; a domain tool cannot share any of these names."));

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
                    "A parameter's name is the C# method's parameter name exactly: the model passes each argument by it."));
                continue;
            }

            if (!parameterNames.Add(parameter.Name))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "This tool declares two parameters with this name.",
                    "The generated method could not declare them and the JSON schema handed to the model would carry one property, not two."));

            ValidateIdentifier(parameter.Name, parameterWhere, "parameter name", findings);

            if (string.IsNullOrWhiteSpace(parameter.Description))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "The parameter has no description.",
                    "Parameter descriptions reach the model through the JSON schema and nowhere else: Morgana refuses at startup a parameter without a [Description]."));

            if (string.IsNullOrWhiteSpace(parameter.Scope))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "The parameter has no scope.",
                    "Every parameter declares [ToolParameter] with 'context' or 'request': Morgana refuses at startup a parameter without one."));
            else if (!KnownScopes.Contains(parameter.Scope, StringComparer.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    $"'{parameter.Scope}' is not a scope.",
                    "A parameter resolving an input declares 'context' or 'request' and nothing else."));

            bool isContext = string.Equals(parameter.Scope, Constants.Scopes.Context, StringComparison.OrdinalIgnoreCase);
            bool isRequest = string.Equals(parameter.Scope, Constants.Scopes.Request, StringComparison.OrdinalIgnoreCase);

            if (parameter.Shared && isRequest)
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "A request parameter is marked as shared.",
                    "Only what the context holds is shared, so a value asked of the user has nothing to share: Morgana refuses the combination at startup."));

            if (!parameter.Required && isContext)
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "A context parameter is optional.",
                    "A context value that nobody holds stops the tool, so a default would never be used: Morgana refuses a context parameter with a default value at startup."));

            if (parameter.Required && optionalSeen)
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "A required parameter follows an optional one.",
                    "C# cannot declare that method signature."));

            optionalSeen |= !parameter.Required;
        }

        if (tool.Returns.Count == 0)
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The tool declares nothing it hands back.",
                "A native tool returns a typed record with properties: Morgana refuses at startup a method that returns nothing, a string or an object."));

        HashSet<string> returnedNames = new(StringComparer.Ordinal);

        foreach (ToolReturnDraft field in tool.Returns)
        {
            string fieldWhere = $"{where}.returns.{field.Name ?? "(unnamed)"}";

            if (string.IsNullOrWhiteSpace(field.Name))
            {
                findings.Add(new ValidationFinding(FindingSeverity.Error, fieldWhere,
                    "The returned field has no name.",
                    "Each field is a property of the returned record and the model reads it under that property's name."));
                continue;
            }

            if (!returnedNames.Add(field.Name))
                findings.Add(new ValidationFinding(FindingSeverity.Error, fieldWhere,
                    "This tool declares two returned fields with this name.",
                    "The record could not declare two properties with one name."));

            ValidateIdentifier(field.Name, fieldWhere, "returned field name", findings);

            // The model reads the property back through camelCase, so the name declared has to be
            // the one that survives the trip out and in.
            if (!string.Equals(JsonNamingPolicy.CamelCase.ConvertName(CodeEmitService.PropertyName(field.Name)), field.Name, StringComparison.Ordinal))
                findings.Add(new ValidationFinding(FindingSeverity.Error, fieldWhere,
                    "The returned field cannot keep this name in C#.",
                    "The record's property is named with its first letter upper-cased and the model reads it back camelCased, so the model would find a different name than the one declared."));

            if (string.IsNullOrWhiteSpace(field.Type))
                findings.Add(new ValidationFinding(FindingSeverity.Error, fieldWhere,
                    "The returned field has no type.",
                    "The record declares each property with its C# type and the generated half is written from it."));
            else if (!ParsesAsType(field.Type))
                findings.Add(new ValidationFinding(FindingSeverity.Error, fieldWhere,
                    $"'{field.Type}' is not a C# type.",
                    "The type is written into the record verbatim and a text that is not a type does not compile."));

            if (field.Name == Constants.Workflows.FailureField && !string.IsNullOrWhiteSpace(field.Type) && field.Type.Trim().TrimEnd('?') != "string")
                findings.Add(new ValidationFinding(FindingSeverity.Error, fieldWhere,
                    $"The '{Constants.Workflows.FailureField}' field is not a string.",
                    "The field named error is the failure and holds why the call failed: Morgana refuses a record whose error property is not text that allows null."));

            if (string.IsNullOrWhiteSpace(field.Description))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, fieldWhere,
                    "The returned field has no description.",
                    "The description reaches the model through the schema of the result and the mock is written from it."));
        }
    }

    /// <summary>
    /// The keywords that name a type, which the keyword list above would otherwise refuse as an identifier.
    /// </summary>
    private static readonly HashSet<string> BuiltInTypeKeywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short", "ushort", "object", "string"
    };

    /// <summary>
    /// Whether a text is a C# type as a record property would write it, with nothing left over.
    /// </summary>
    /// <remarks>
    /// Shape only, like the identifier check: a dotted name or a built-in keyword with optional generic
    /// arguments, array brackets and a trailing question mark. Whether the type exists is the compiler's to say.
    /// </remarks>
    public static bool ParsesAsType(string text)
    {
        int position = 0;

        return ReadType(text, ref position) && position == text.Length;
    }

    /// <summary>
    /// Reads one type from the position on, skipping spaces. Leaves the position after it.
    /// </summary>
    private static bool ReadType(string text, ref int position)
    {
        if (!ReadName(text, ref position))
            return false;

        SkipSpaces(text, ref position);

        if (position < text.Length && text[position] == '<')
        {
            position++;

            while (true)
            {
                SkipSpaces(text, ref position);
                if (!ReadType(text, ref position))
                    return false;

                SkipSpaces(text, ref position);

                if (position >= text.Length || text[position] != ',')
                    break;

                position++;
            }

            if (position >= text.Length || text[position] != '>')
                return false;

            position++;
        }

        // Array brackets and the question mark may follow in any order that C# allows: int[]?, int?[] and int[][].
        while (true)
        {
            SkipSpaces(text, ref position);

            if (position < text.Length && text[position] == '?')
            {
                position++;
            }
            else if (position < text.Length && text[position] == '[')
            {
                position++;

                while (position < text.Length && text[position] == ',')
                    position++;

                if (position >= text.Length || text[position] != ']')
                    return false;

                position++;
            }
            else
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Reads a built-in keyword or a dotted name whose every segment is an identifier.
    /// </summary>
    private static bool ReadName(string text, ref int position)
    {
        while (true)
        {
            SkipSpaces(text, ref position);
            int start = position;

            if (position >= text.Length || !(char.IsLetter(text[position]) || text[position] == '_'))
                return false;

            while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_'))
                position++;

            string segment = text[start..position];

            if (ReservedWords.Contains(segment) && !BuiltInTypeKeywords.Contains(segment))
                return false;

            if (position < text.Length && text[position] == '.')
            {
                position++;
                continue;
            }

            return true;
        }
    }

    /// <summary>
    /// Moves the position past any spaces.
    /// </summary>
    private static void SkipSpaces(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;
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
