using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;
using Morgana.AI.Services;

namespace Alembic.Services;

/// <summary>
/// Checks the Draft by reading it alone, restating rules that Morgana would otherwise enforce only after deployment.
/// </summary>
public class DraftValidationService : IDraftValidationService
{
    /// <summary>The framework's own prompt IDs, which no domain intent may take since prompt resolution would find two matches.</summary>
    private static readonly string[] FrameworkPromptIds =
        ["Morgana", "Classifier", "Guard", "Presentation", "ChannelAdapter"];

    /// <summary>The two scopes a tool parameter may declare.</summary>
    private static readonly string[] KnownScopes = [Constants.Scopes.Context, Constants.Scopes.Request];

    /// <summary>The C# keywords that a domain author plausibly reaches for when naming a tool or parameter and that cannot stand as identifiers.</summary>
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
        // Every finding of the domain, collected family by family and agent by agent.
        List<ValidationFinding> findings = [];

        // The domain-wide families come first, each filed under its own check.
        Collect(findings, ValidationCheck.Intents, found => ValidateIntents(draft, found));
        Collect(findings, ValidationCheck.Routing, found => ValidateIntentAgentPairing(draft, found));
        Collect(findings, ValidationCheck.Colleagues, found => ValidateConsultations(draft, found));

        // Each agent is checked beside the intent that routes to it, since its territory and the routing phrase are read against each other.
        foreach (AgentDraft agent in draft.Agents)
        {
            // The findings of this agent alone, filed afterwards under the agent and the family they belong to.
            List<ValidationFinding> agentFindings = [];

            ValidateAgent(agent, draft.Intents.FirstOrDefault(i =>
                string.Equals(i.Name, agent.ID, StringComparison.OrdinalIgnoreCase)), agentFindings);

            findings.AddRange(agentFindings.Select(finding => finding with { Agent = agent.ID, Check = CheckOf(finding) }));
        }

        // Errors come before warnings so the client meets what stops them first; the domain's own order holds within each group.
        return [.. findings.OrderByDescending(f => f.Severity)];
    }

    /// <summary>Runs one family of checks and files what it found under that family and the agent it names.</summary>
    private static void Collect(List<ValidationFinding> findings, ValidationCheck check, Action<List<ValidationFinding>> validate)
    {
        // The findings of this family alone, before they are filed.
        List<ValidationFinding> familyFindings = [];
        validate(familyFindings);

        // The agent an intent reaches carries the intent's own name, which the finding quotes in its location.
        findings.AddRange(familyFindings.Select(finding => finding with
        {
            Check = check,
            Agent = QuotedName.Match(finding.Where) is { Success: true } quote ? quote.Groups[1].Value : null
        }));
    }

    /// <summary>Picks the name that an intent or agent finding quotes in its location.</summary>
    private static readonly System.Text.RegularExpressions.Regex QuotedName =
        new(@"^(?:intent|agent) '([^']*)'", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Reads which family an agent's own finding belongs to off the location its check wrote.</summary>
    /// <remarks>A class name is the code's and an agent.tool address is the toolkit's; anything else is the agent's prose.</remarks>
    private static ValidationCheck CheckOf(ValidationFinding finding) =>
        finding.Where.EndsWith("class name", StringComparison.Ordinal) ? ValidationCheck.Code
        : finding.Where.StartsWith("agent '", StringComparison.Ordinal) ? ValidationCheck.Agents
        : ValidationCheck.Tools;

    /// <summary>Checks each intent for a name that is present, unique and free of the framework's prompt IDs and carries the fields its button needs.</summary>
    private static void ValidateIntents(DomainDraft draft, List<ValidationFinding> findings)
    {
        // The names met so far, to catch two intents that Morgana would read as one.
        HashSet<string> intentNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (IntentDraft intent in draft.Intents)
        {
            // The location every finding about this intent quotes.
            string where = $"intent '{intent.Name ?? "(unnamed)"}'";

            // Without a name nothing else about the intent can be checked.
            if (string.IsNullOrWhiteSpace(intent.Name))
            {
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    "The intent has no name.",
                    "An intent is addressed by name: it keys the agent prompt, the [HandlesIntent] attribute and the classifier's own vocabulary."));
                continue;
            }

            // A repeated name is one intent to Morgana.
            if (!intentNames.Add(intent.Name))
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    "Two intents carry this name.",
                    "Prompt IDs are matched case-insensitively, so two intents differing only in case are one intent to Morgana."));

            // A name taken from the framework makes prompt resolution find two matches.
            if (FrameworkPromptIds.Contains(intent.Name, StringComparer.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    $"'{intent.Name}' is one of the framework's own prompt IDs.",
                    "ConfigurationPromptResolverService resolves prompts with SingleOrDefault across both layers: a domain ID colliding with a framework one throws at resolution rather than silently shadowing it."));

            // Without a description the classifier has only the name to route on.
            if (string.IsNullOrWhiteSpace(intent.Description))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    "The intent has no description.",
                    "The description is the only thing the classifier reads about this intent; without one it can only match on the name."));

            // Without a label the intent's button shows nothing.
            if (string.IsNullOrWhiteSpace(intent.Label))
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    "The intent has no label.",
                    "Morgana refuses to start: every intent is offered as a button and the label is what the button shows."));

            // Without a default value the intent's button sends nothing.
            if (string.IsNullOrWhiteSpace(intent.DefaultValue))
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    "The intent has no default value.",
                    "Morgana refuses to start: every intent is offered as a button and the default value is what pressing it sends."));
        }
    }

    /// <summary>Checks that every intent has an agent and every agent an intent, as the startup registry does.</summary>
    private static void ValidateIntentAgentPairing(DomainDraft draft, List<ValidationFinding> findings)
    {
        // The agents the domain declares, to find the intents that nobody handles.
        HashSet<string> agentIds = new(
            draft.Agents.Where(a => !string.IsNullOrWhiteSpace(a.ID)).Select(a => a.ID!),
            StringComparer.OrdinalIgnoreCase);

        // The intents the domain declares, to find the agents that nothing routes to.
        HashSet<string> intentNames = new(
            draft.Intents.Where(i => !string.IsNullOrWhiteSpace(i.Name)).Select(i => i.Name!),
            StringComparer.OrdinalIgnoreCase);

        // An intent without an agent leaves the classifier routing to nothing.
        findings.AddRange(
            draft.Intents.Where(i => !string.IsNullOrWhiteSpace(i.Name)
                                       && !agentIds.Contains(i.Name!))
                         .Select(intent => new ValidationFinding(FindingSeverity.Error, $"intent '{intent.Name}'", "No agent handles this intent.", "HandlesIntentAgentRegistryService checks this in both directions at startup and throws on a mismatch.")));

        // An agent without an intent is unreachable.
        findings.AddRange(
            draft.Agents.Where(a => !string.IsNullOrWhiteSpace(a.ID)
                                      && !intentNames.Contains(a.ID!))
                        .Select(agent => new ValidationFinding(FindingSeverity.Error, $"agent '{agent.ID}'", "No intent declares this agent.", "An agent nothing routes to is unreachable and the startup registry treats it as a configuration error rather than dead weight.")));
    }

    /// <summary>Checks every declared colleague against the domain: it must exist, differ from the asker and not start a chain longer than one hop.</summary>
    /// <remarks>A colleague at another system is checked only for having a name, since its domain is not visible here.</remarks>
    private static void ValidateConsultations(DomainDraft draft, List<ValidationFinding> findings)
    {
        // The agents of this domain that a colleague may name.
        HashSet<string> agentIds = new(
            draft.Agents.Where(a => !string.IsNullOrWhiteSpace(a.ID)).Select(a => a.ID!),
            StringComparer.OrdinalIgnoreCase);

        foreach (AgentDraft agent in draft.Agents)
        {
            // The location every finding about this agent quotes.
            string where = $"agent '{agent.ID ?? "(unnamed)"}'";

            foreach (Morgana.AI.Records.PeerReference colleague in agent.Code.Consults)
            {
                // A colleague at another system is checked for its name alone.
                if (colleague.Instance is not null)
                {
                    // A blank system name matches no partner entry.
                    if (string.IsNullOrWhiteSpace(colleague.Instance))
                        findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                            $"It consults '{colleague.Intent}' at a system with no name.",
                            "The name is matched against an entry under Morgana:AgentToAgent:Partners and a blank one matches nothing: startup refuses it."));

                    // The remaining checks need the colleague's agent in this domain.
                    continue;
                }

                // An agent cannot consult itself.
                if (string.Equals(colleague.Intent, agent.ID, StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                        $"It declares itself as a colleague ('{colleague.Intent}').",
                        "The startup registry refuses a [ConsultsAgent] naming the declaring agent's own intent: an agent cannot be its own second opinion."));
                    continue;
                }

                // A colleague that no agent of the domain handles fails startup.
                if (!agentIds.Contains(colleague.Intent))
                {
                    findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                        $"It consults '{colleague.Intent}', which no agent of this domain handles.",
                        "A consultation is resolved when the agent is created, so a name nothing answers is startup-fatal rather than a colleague that quietly never appears."));
                    continue;
                }

                // The colleague's agent, whose own colleagues decide whether the chain is longer than one hop.
                AgentDraft? colleagueAgent = draft.Agents.FirstOrDefault(a => string.Equals(a.ID, colleague.Intent, StringComparison.OrdinalIgnoreCase));

                // The far agent's peer functions are withheld while it answers, so a second hop never happens.
                if (colleagueAgent?.Code.Consults.Count > 0)
                    findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                        $"It consults '{colleague.Intent}', which consults a colleague of its own.",
                        "While it answers a consultation the framework withholds its peer functions, so the second hop never happens: whatever the far agent would have asked for is not in the answer this one gets."));
            }
        }
    }

    /// <summary>Reduces a sentence to its letters and digits, so two phrasings of one sentence compare equal.</summary>
    private static string Compact(string? text) =>
        new string([.. (text ?? string.Empty).Where(char.IsLetterOrDigit)]).ToLowerInvariant();

    /// <summary>Checks one agent's prose, its class names and its whole toolkit.</summary>
    /// <param name="intent">The intent that routes to this agent, whose description the territory is read against; <c>null</c> when none does.</param>
    private static void ValidateAgent(AgentDraft agent, IntentDraft? intent, List<ValidationFinding> findings)
    {
        // The location every finding about this agent quotes.
        string where = $"agent '{agent.ID ?? "(unnamed)"}'";

        // Without an ID the agent pairs with no prompt and no intent.
        if (string.IsNullOrWhiteSpace(agent.ID))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The agent has no ID.",
                "The ID is what pairs a prompt with its intent and its [HandlesIntent] attribute."));

        // Without a Target the agent has no purpose of its own.
        if (string.IsNullOrWhiteSpace(agent.Target))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The agent has no Target.",
                "Target is the domain layer's first section and states what the agent is for; composed empty, the agent inherits only the framework's generic purpose.") { Step = InterviewStep.AgentTarget });

        // Without a Territory a colleague has no sentence to weigh its question against.
        if (string.IsNullOrWhiteSpace(agent.Territory))
            findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                "The agent has nothing to say to a colleague consulting it.",
                "Territory is what a colleague reads to decide whether a question is this agent's; without it the card falls back to the intent description, which is a routing phrase written for the classifier.") { Step = InterviewStep.AgentTerritory });

        // A colleague reads the territory and nothing else about this agent.
        // Two useless forms are decidable here: an inventory of the agent's tools and the classifier's routing phrase.
        string? territory = AgentRows.Plain(agent.Territory);

        if (!string.IsNullOrWhiteSpace(territory))
        {
            // The first of the agent's own tools that the territory names, if any.
            string? toolName = agent.Tools
                .Select(tool => tool.Name)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)
                                        && territory.Contains(name!, StringComparison.OrdinalIgnoreCase));

            // A territory that names a tool reads as an inventory.
            if (toolName is not null)
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    $"What this agent publishes to a colleague names its own tool '{toolName}'.",
                    "Territory states a territory: a colleague handed an inventory of functions rules its question out instead of asking it.") { Step = InterviewStep.AgentTerritory });

            // A territory identical to the routing phrase is the classifier's sentence recycled.
            if (intent is not null && string.Equals(Compact(territory), Compact(intent.Description), StringComparison.OrdinalIgnoreCase))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                    "What this agent publishes to a colleague is its own routing description.",
                    "The intent description tells the classifier which user utterances land here, never what this agent answers for — a caller reading it back learns nothing it could ask about.") { Step = InterviewStep.AgentTerritory });
        }

        // Without Instructions the agent adds no domain constraint of its own.
        if (string.IsNullOrWhiteSpace(agent.Instructions))
            findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                "The agent has no Instructions.",
                "Legal — the framework layer already governs how a turn is formed — but it means this agent adds no domain constraint of its own.") { Step = InterviewStep.AgentInstructions });

        // Without Formatting nothing says how the agent presents its own tools' output.
        if (string.IsNullOrWhiteSpace(agent.Formatting))
            findings.Add(new ValidationFinding(FindingSeverity.Warning, where,
                "The agent has no Formatting.",
                "Formatting is where an agent says how its own tools' output should be presented, which no global policy can know for it.") { Step = InterviewStep.AgentFormatting });

        // An agent with no native tool and an inferred class set raise no finding: they cannot be told apart from an MCP-only agent or a fresh import.

        // The class names become C# verbatim.
        ValidateIdentifier(agent.Code.AgentClassName, $"{where} class name", "class name", findings);
        ValidateIdentifier(agent.Code.ToolClassName, $"{where} tool class name", "class name", findings);

        // The tool names met so far, to catch two tools of one agent with one name.
        HashSet<string> toolNames = new(StringComparer.Ordinal);

        // Reply is every agent's and each workflow of this agent has a launcher: a domain tool sharing one of
        // these names would be offered twice to the same agent.
        HashSet<string> reservedToolNames =
        [
            Constants.Tools.Reply,
            Constants.Tools.Present,
            .. agent.Workflows
                .Where(workflow => !string.IsNullOrWhiteSpace(workflow.Name))
                .Select(workflow => Constants.Workflows.LauncherPrefix + workflow.Name)
        ];

        // Each tool is judged on its own and against the names the agent already has.
        foreach (ToolDraft tool in agent.Tools)
            ValidateTool(agent, tool, toolNames, reservedToolNames, findings);

        // The workflows are judged last since they refer to the tools just checked.
        ValidateWorkflows(agent, where, findings);

        // The menu is judged after both its kinds of entry have been.
        ValidateOfferings(agent, where, findings);
    }

    /// <summary>Replays the framework's own startup check of an agent's menu over the Draft.</summary>
    /// <remarks>A nameless tool or workflow is left out, since the checks above have already reported it and the framework's message could not name it.</remarks>
    private static void ValidateOfferings(AgentDraft agent, string where, List<ValidationFinding> findings)
    {
        // The menu is written by the workflows pass when the agent has any workflow and by the toolkit pass otherwise.
        InterviewStep menuStep = agent.Workflows.Count > 0 ? InterviewStep.AgentWorkflows : InterviewStep.AgentToolkit;

        // The framework's own refusals for the menu against the agent's named tools and workflows.
        IEnumerable<string> refusals = HandlesIntentAgentRegistryService.ValidateOfferings(
            agent.ID ?? string.Empty,
            [.. agent.Tools.Where(tool => !string.IsNullOrWhiteSpace(tool.Name)).Select(DraftProjection.ToToolDefinition)],
            [.. agent.Workflows.Where(workflow => !string.IsNullOrWhiteSpace(workflow.Name)).Select(DraftProjection.ToWorkflowDefinition)]);

        // Each refusal is reported with the framework's own wording.
        foreach (string refusal in refusals)
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                refusal,
                "Morgana refuses it at startup: an agent's menu is checked against its tools and workflows before the first conversation.") { Step = menuStep });
    }

    /// <summary>Replays the framework's own startup check of an agent's workflows over the Draft.</summary>
    /// <remarks>A workflow or step without a name is reported here and not handed to the framework, whose message could not name it.</remarks>
    private static void ValidateWorkflows(AgentDraft agent, string where, List<ValidationFinding> findings)
    {
        // The workflows complete enough for the framework's check to speak of them by name.
        List<WorkflowDraft> projections = [];

        foreach (WorkflowDraft workflow in agent.Workflows)
        {
            // A nameless workflow or step is reported and skipped.
            if (string.IsNullOrWhiteSpace(workflow.Name) || workflow.Steps.Any(step => string.IsNullOrWhiteSpace(step.Name)))
            {
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    $"A workflow of this agent is too incomplete to check: {(string.IsNullOrWhiteSpace(workflow.Name) ? "it has no name" : $"'{workflow.Name}' has a step with no name")}.",
                    "Morgana refuses a workflow or a step without a name at startup: the model starts a workflow by its name and every link between steps is made by theirs.") { Step = InterviewStep.AgentWorkflows });
                continue;
            }

            // The emitted class declares a field per step and a property per carried value, so one name for both does not compile.
            HashSet<string> carriedProperties = [.. DraftProjection.CarriedNames(workflow).Select(CodeEmitService.PropertyName)];

            // Each step named like a carried value is reported.
            foreach (Records.WorkflowStep step in workflow.Steps.Where(step => carriedProperties.Contains(step.Name)))
                findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                    $"Workflow '{workflow.Name}' has a step '{step.Name}' named like a value that one of its edges carries.",
                    "The workflow's class would declare the step and the carried value under one name and would not compile.") { Step = InterviewStep.AgentWorkflows });

            // The workflow is complete enough to be checked by the framework.
            projections.Add(workflow);
        }

        // The framework's own refusals for the workflows against the agent's tools and MCP servers.
        IEnumerable<string> refusals = HandlesIntentAgentRegistryService.ValidateWorkflows(
            agent.ID ?? string.Empty,
            [.. projections.Select(DraftProjection.ToWorkflowDefinition)],
            agent.Tools.Select(DraftProjection.ToToolDefinition),
            agent.Code.MCPServers.Count > 0);

        // Each refusal is reported with the framework's own wording.
        foreach (string refusal in refusals)
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                refusal,
                "Morgana refuses it at startup: workflows are checked against the agent's tools and against one another before the first conversation.") { Step = InterviewStep.AgentWorkflows });
    }

    /// <summary>Checks one tool's name and description then hands its parameters and its returned fields to their own checks.</summary>
    /// <param name="toolNames">The names of the tools already met on this agent.</param>
    /// <param name="reservedToolNames">The function names that every agent already receives.</param>
    /// <param name="toolkit">The agent's findings, to which each tool finding is added under the Toolkit step.</param>
    private static void ValidateTool(AgentDraft agent, ToolDraft tool, HashSet<string> toolNames, IReadOnlySet<string> reservedToolNames, List<ValidationFinding> toolkit)
    {
        // Every finding about a tool is rewritten in the Toolkit step.
        ToolkitFindings findings = new(toolkit);

        // The location every finding about this tool quotes.
        string where = $"{agent.ID}.{tool.Name ?? "(unnamed)"}";

        // Without a name nothing else about the tool can be checked.
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The tool has no name.",
                "A tool's name is its C# method name exactly: the model calls the tool by it."));
            return;
        }

        // A repeated name collides at registration.
        if (!toolNames.Add(tool.Name))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "This agent declares two tools with this name.",
                "Tools are registered by name per agent, so the second registration replaces or collides with the first."));

        // A reserved name would be offered twice to the same agent.
        if (reservedToolNames.Contains(tool.Name))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                $"'{tool.Name}' is a function that this agent already receives.",
                $"Every agent receives {Constants.Tools.Reply} and a function named {Constants.Workflows.LauncherPrefix} followed by the name of each of its workflows; a domain tool cannot share any of these names."));

        // The tool name becomes the C# method name.
        ValidateIdentifier(tool.Name, where, "tool name", findings);

        // Without a description the model decides whether to call the tool from its name alone.
        if (string.IsNullOrWhiteSpace(tool.Description))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The tool has no description.",
                "The description is what the model reads when it decides whether to call this tool at all; an empty one leaves the decision to the name."));

        // The parameter list and the returned record are the tool's two other declarations and each is judged on its own rules.
        ValidateParameters(tool, where, findings);
        ValidateReturns(tool, where, findings);
    }

    /// <summary>Checks the parameter list of one tool: names, descriptions, scopes and the order C# can declare.</summary>
    private static void ValidateParameters(ToolDraft tool, string where, ToolkitFindings findings)
    {
        // The parameter names met so far, to catch two parameters with one name.
        HashSet<string> parameterNames = new(StringComparer.Ordinal);

        // The index is kept since a required parameter is judged against those before it.
        for (int index = 0; index < tool.Parameters.Count; index++)
        {
            // The parameter under judgement, always present since the index runs inside the list.
            ToolParameterDraft parameter = tool.Parameters[index];

            // The location every finding about this parameter quotes.
            string parameterWhere = $"{where}.{parameter.Name ?? "(unnamed)"}";

            // Without a name nothing else about the parameter can be checked.
            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "The parameter has no name.",
                    "A parameter's name is the C# method's parameter name exactly: the model passes each argument by it."));
                continue;
            }

            // A repeated name cannot be declared in the method nor in the schema.
            if (!parameterNames.Add(parameter.Name))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "This tool declares two parameters with this name.",
                    "The generated method could not declare them and the JSON schema handed to the model would carry one property, not two."));

            // The parameter name becomes the C# parameter name.
            ValidateIdentifier(parameter.Name, parameterWhere, "parameter name", findings);

            // Without a description the model receives the parameter unexplained.
            if (string.IsNullOrWhiteSpace(parameter.Description))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "The parameter has no description.",
                    "Parameter descriptions reach the model through the JSON schema and nowhere else: Morgana refuses at startup a parameter without a [Description]."));

            // A parameter must say whether the system or the user supplies it.
            if (string.IsNullOrWhiteSpace(parameter.Scope))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "The parameter has no scope.",
                    "Every parameter declares [ToolParameter] with 'context' or 'request': Morgana refuses at startup a parameter without one."));
            // Any other scope is unknown to Morgana.
            else if (!IsScope(parameter.Scope))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    $"'{parameter.Scope}' is not a scope.",
                    "A parameter resolving an input declares 'context' or 'request' and nothing else."));

            // A value asked of the user has nothing in the context to share.
            if (IsSharedRequest(parameter.Scope, parameter.Shared))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "A request parameter is marked as shared.",
                    "Only what the context holds is shared, so a value asked of the user has nothing to share: Morgana refuses the combination at startup."));

            // A context value that nobody holds stops the tool, so it cannot be optional.
            if (IsOptionalContext(parameter.Scope, parameter.Required))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "A context parameter is optional.",
                    "A context value that nobody holds stops the tool, so a default would never be used: Morgana refuses a context parameter with a default value at startup."));

            // The generated signature would not compile.
            if (RequiredFollowsOptional(tool.Parameters, index))
                findings.Add(new ValidationFinding(FindingSeverity.Error, parameterWhere,
                    "A required parameter follows an optional one.",
                    "C# cannot declare that method signature."));
        }
    }

    /// <summary>Checks the fields one tool hands back: names, types and the failure field.</summary>
    private static void ValidateReturns(ToolDraft tool, string where, ToolkitFindings findings)
    {
        // A native tool must return a typed record with at least one property.
        if (tool.Returns.Count == 0)
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                "The tool declares nothing it hands back.",
                "A native tool returns a typed record with properties: Morgana refuses at startup a method that returns nothing, a string or an object."));

        // The field names met so far, to catch two fields with one name.
        HashSet<string> returnedNames = new(StringComparer.Ordinal);

        foreach (ToolReturnDraft field in tool.Returns)
        {
            // The location every finding about this field quotes.
            string fieldWhere = $"{where}.returns.{field.Name ?? "(unnamed)"}";

            // Without a name nothing else about the field can be checked.
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                findings.Add(new ValidationFinding(FindingSeverity.Error, fieldWhere,
                    "The returned field has no name.",
                    "Each field is a property of the returned record and the model reads it under that property's name."));
                continue;
            }

            // A repeated name cannot be declared twice in the record.
            if (!returnedNames.Add(field.Name))
                findings.Add(new ValidationFinding(FindingSeverity.Error, fieldWhere,
                    "This tool declares two returned fields with this name.",
                    "The record could not declare two properties with one name."));

            // The field name becomes a record property.
            ValidateIdentifier(field.Name, fieldWhere, "returned field name", findings);

            // The model reads the property back camelCased, so the declared name must survive the round trip.
            if (!KeepsNameInCSharp(field.Name))
                findings.Add(new ValidationFinding(FindingSeverity.Error, fieldWhere,
                    "The returned field cannot keep this name in C#.",
                    "The record's property is named with its first letter upper-cased and the model reads it back camelCased, so the model would find a different name than the one declared."));

            // The record cannot declare a property without a type.
            if (string.IsNullOrWhiteSpace(field.Type))
                findings.Add(new ValidationFinding(FindingSeverity.Error, fieldWhere,
                    "The returned field has no type.",
                    "The record declares each property with its C# type and the generated half is written from it."));
            // The type is written into the record verbatim, so its shape must be a type.
            else if (!ParsesAsType(field.Type))
                findings.Add(new ValidationFinding(FindingSeverity.Error, fieldWhere,
                    $"'{field.Type}' is not a C# type.",
                    "The type is written into the record verbatim and a text that is not a type does not compile."));

            // The failure field holds the reason of a failure, which is text.
            if (IsNonTextFailureField(field.Name, field.Type))
                findings.Add(new ValidationFinding(FindingSeverity.Error, fieldWhere,
                    $"The '{Constants.Workflows.FailureField}' field is not a string.",
                    "The field named error is the failure and holds why the call failed: Morgana refuses a record whose error property is not text that allows null."));

            // Without a description the model and the mock author have nothing to read the field by.
            if (string.IsNullOrWhiteSpace(field.Description))
                findings.Add(new ValidationFinding(FindingSeverity.Warning, fieldWhere,
                    "The returned field has no description.",
                    "The description reaches the model through the schema of the result and the mock is written from it."));
        }
    }

    /// <summary>The keywords that name a type, which the keyword list would otherwise refuse as identifiers.</summary>
    private static readonly HashSet<string> BuiltInTypeKeywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short", "ushort", "object", "string"
    };

    /// <summary>Whether a text has the shape of a C# type as a record property would write it.</summary>
    /// <remarks>Only the shape is checked: whether the type exists is the compiler's to say.</remarks>
    public static bool ParsesAsType(string text)
    {
        // The reading starts at the first character.
        int position = 0;

        // The type must end exactly where the text ends.
        return ReadType(text, ref position) && position == text.Length;
    }

    /// <summary>Reads one type from the position on and leaves the position after it.</summary>
    private static bool ReadType(string text, ref int position)
    {
        // A type starts with a name.
        if (!ReadName(text, ref position))
            return false;

        // Spaces may separate the name from the generic arguments.
        SkipSpaces(text, ref position);

        // Generic arguments are types separated by commas.
        if (position < text.Length && text[position] == '<')
        {
            // The opening angle bracket belongs to the type.
            position++;

            while (true)
            {
                // Spaces may precede each argument.
                SkipSpaces(text, ref position);

                // Each argument is a type of its own.
                if (!ReadType(text, ref position))
                    return false;

                // Spaces may follow each argument.
                SkipSpaces(text, ref position);

                // Without a comma the argument list is over.
                if (position >= text.Length || text[position] != ',')
                    break;

                // The comma separating two arguments is consumed.
                position++;
            }

            // The argument list must close.
            if (position >= text.Length || text[position] != '>')
                return false;

            // The closing angle bracket ends the generic arguments.
            position++;
        }

        // Array brackets and the question mark may follow in any order that C# allows: int[]?, int?[] and int[][].
        while (true)
        {
            // Spaces may precede each suffix.
            SkipSpaces(text, ref position);

            // A question mark makes the type nullable.
            if (position < text.Length && text[position] == '?')
            {
                // The question mark is consumed as part of the type.
                position++;
            }
            // Brackets make it an array; commas inside them add dimensions.
            else if (position < text.Length && text[position] == '[')
            {
                // The opening bracket belongs to the array suffix.
                position++;

                // Each comma inside the brackets adds one dimension.
                while (position < text.Length && text[position] == ',')
                    position++;

                // The brackets must close.
                if (position >= text.Length || text[position] != ']')
                    return false;

                // The closing bracket ends this array suffix.
                position++;
            }
            // Nothing else may follow the type.
            else
            {
                // The type is complete: the suffixes are over.
                return true;
            }
        }
    }

    /// <summary>Reads a built-in keyword or a dotted name whose every segment is an identifier.</summary>
    private static bool ReadName(string text, ref int position)
    {
        while (true)
        {
            // Spaces may surround each segment of a dotted name.
            SkipSpaces(text, ref position);

            // The segment starts here.
            int start = position;

            // A segment starts with a letter or an underscore.
            if (position >= text.Length || !(char.IsLetter(text[position]) || text[position] == '_'))
                return false;

            // The segment runs through letters digits and underscores.
            while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_'))
                position++;

            // The segment is checked against the reserved words.
            string segment = text[start..position];

            // A keyword is refused unless it names a built-in type.
            if (ReservedWords.Contains(segment) && !BuiltInTypeKeywords.Contains(segment))
                return false;

            // A dot continues the name with another segment.
            if (position < text.Length && text[position] == '.')
            {
                // The dot is consumed and the next segment is read.
                position++;
                continue;
            }

            // Any other character ends the name.
            return true;
        }
    }

    /// <summary>Moves the position past any spaces.</summary>
    private static void SkipSpaces(string text, ref int position)
    {
        // Every whitespace character before the next token is skipped.
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;
    }

    /// <summary>A finding list that files every tool finding under the Toolkit step, where the client rewrites it.</summary>
    private sealed class ToolkitFindings(List<ValidationFinding> findings)
    {
        /// <summary>The agent's own finding list the tool findings land in.</summary>
        private List<ValidationFinding> Items { get; } = findings;

        /// <summary>Adds a finding, marked as the Toolkit step's to rewrite.</summary>
        public void Add(ValidationFinding finding) => Items.Add(finding with { Step = InterviewStep.AgentToolkit });

        /// <summary>The list underneath, for the identifier check shared with the agent's own names.</summary>
        public static implicit operator List<ValidationFinding>(ToolkitFindings toolkit) => toolkit.Items;
    }

    /// <summary>Whether a name has the shape of a C# identifier: a letter or underscore first then letters digits or underscores.</summary>
    public static bool IsIdentifierShape(string? name) =>
        !string.IsNullOrEmpty(name)
        && (char.IsLetter(name[0]) || name[0] == '_')
        && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>Whether a name can stand as a C# identifier: it has the shape and is not a keyword.</summary>
    public static bool IsIdentifier(string? name) => IsIdentifierShape(name) && !IsKeyword(name!);

    /// <summary>Whether a name is one of the keywords that cannot stand bare as an identifier.</summary>
    public static bool IsKeyword(string name) => ReservedWords.Contains(name);

    /// <summary>Whether a scope is one of the two a parameter may declare.</summary>
    internal static bool IsScope(string? scope) => KnownScopes.Contains(scope, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a parameter asked of the user is marked as shared, which only a context value can be.</summary>
    internal static bool IsSharedRequest(string? scope, bool shared) =>
        shared && string.Equals(scope, Constants.Scopes.Request, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a context parameter is optional, which a value the context must hold can never be.</summary>
    internal static bool IsOptionalContext(string? scope, bool required) =>
        !required && string.Equals(scope, Constants.Scopes.Context, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the parameter at an index is required yet follows an optional one, a signature C# cannot declare.</summary>
    internal static bool RequiredFollowsOptional(IReadOnlyList<ToolParameterDraft> parameters, int index) =>
        parameters[index].Required && parameters.Take(index).Any(earlier => !earlier.Required);

    /// <summary>Whether a returned field's name comes back unchanged after the record's property is camelCased for the model.</summary>
    internal static bool KeepsNameInCSharp(string name) =>
        string.Equals(JsonNamingPolicy.CamelCase.ConvertName(CodeEmitService.PropertyName(name)), name, StringComparison.Ordinal);

    /// <summary>Whether a returned field is the failure field yet declares a type other than text.</summary>
    internal static bool IsNonTextFailureField(string name, string? type) =>
        string.Equals(name, Constants.Workflows.FailureField, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(type)
        && type.Trim().TrimEnd('?') != "string";

    /// <summary>Checks that a name written by the client can be a C# identifier: a space, a hyphen, a leading digit or a keyword are caught while they cost nothing to change.</summary>
    /// <param name="what">What the name is in the domain, quoted in the finding.</param>
    private static void ValidateIdentifier(string? name, string where, string what, List<ValidationFinding> findings)
    {
        // A missing name is reported by the caller as its own finding.
        if (string.IsNullOrWhiteSpace(name))
            return;

        // A name with spaces or symbols cannot be written into C#.
        if (!IsIdentifierShape(name))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                $"'{name}' cannot be a C# identifier.",
                $"The {what} becomes C# verbatim: it must start with a letter or underscore and carry only letters, digits and underscores."));
        // A keyword cannot be used bare as an identifier.
        else if (IsKeyword(name))
            findings.Add(new ValidationFinding(FindingSeverity.Error, where,
                $"'{name}' is a C# keyword.",
                $"The {what} becomes C# verbatim and a keyword cannot be used bare as an identifier."));
    }
}
