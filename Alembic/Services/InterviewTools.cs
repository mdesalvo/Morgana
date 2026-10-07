using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Model;
using Morgana.Contracts;
using Morgana.AI;

namespace Alembic.Services;

/// <summary>
/// The tools Alembic calls while conducting a pass.
/// </summary>
/// <remarks>
/// <para>
/// Every method here returns a sentence <b>to the model</b>, not to the client. That return channel
/// is the point of having tools at all rather than a structured reply: a section that comes back
/// the wrong shape is reported to Alembic in the same turn and it corrects itself before the
/// client ever sees anything. A single malformed structured reply, by contrast, costs the client a
/// turn of their own interview.
/// </para>
/// <para>
/// The write tools also carry the <b>section labels</b>. Both composed layers use the same four,
/// which is exactly why the framework fences them, so a domain layer arriving unlabelled leaves
/// half the prompt without the markers the other half has. A label says which section this is, not
/// what it means: it is structure and structure is not left to a model remembering a rule.
/// </para>
/// <para>
/// Which of these exist in a given pass is decided by <c>alembic.json</c>, not by prose. The
/// functional pass has no tool for an agent's instructions or formatting, so it cannot write them
/// — the constraint is the absence of a tool rather than a sentence asking for restraint.
/// </para>
/// </remarks>
public class InterviewTools
{
    /// <summary>
    /// The intent name the framework reserves for the classifier's fallback. No authored agent may
    /// take it.
    /// </summary>
    private const string ReservedFallbackIntent = Constants.Intents.Other;

    /// <summary>
    /// The scope of a parameter Morgana resolves from the session's own context variables.
    /// </summary>
    private const string ContextScope = Constants.Scopes.Context;

    /// <summary>
    /// The scope of a parameter the agent obtains from the user in conversation.
    /// </summary>
    private const string RequestScope = Constants.Scopes.Request;

    /// <summary>The interview these tools write into — see the constructor.</summary>
    private readonly InterviewState interviewState;

    /// <summary>
    /// Whether this pass has already been handed Morgana's own layer of the composed prompt.
    /// </summary>
    /// <remarks>
    /// One instance of these tools serves one pass, so this is exactly "has this pass seen it": a
    /// step that reads the composed prompt twice pays for her half once.
    /// </remarks>
    private bool framework;

    /// <summary>The domain being built or evolved — see the constructor.</summary>
    private readonly IDraftStateService draftStateService;

    /// <summary>The deterministic checks — see the constructor.</summary>
    private readonly IDraftValidationService draftValidationService;

    /// <summary>Composes the prompt the authored agent will really read — see the constructor.</summary>
    private readonly IRecapService recapService;

    /// <summary>
    /// Binds the toolset to one interview.
    /// </summary>
    /// <param name="interviewState">The interview these tools write into.</param>
    /// <param name="draftStateService">The domain being built or evolved.</param>
    /// <param name="draftValidationService">The deterministic checks.</param>
    /// <param name="recapService">Composes the prompt the authored agent will really read.</param>
    public InterviewTools(
        InterviewState interviewState,
        IDraftStateService draftStateService,
        IDraftValidationService draftValidationService,
        IRecapService recapService)
    {
        this.interviewState = interviewState;
        this.draftStateService = draftStateService;
        this.draftValidationService = draftValidationService;
        this.recapService = recapService;
    }

    /// <summary>
    /// Puts one kind of request on the domain map, or revises one already there.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The map is the interview's spine: one entry becomes one intent and one agent and the three
    /// later passes run once down the list. Revising keeps the entry's place, because the order is
    /// the order the client thought of their own business in and the interview walks it in that
    /// order.
    /// </para>
    /// <para>
    /// All four fields of an intent and the two that arrive later arrive here too. The button and
    /// the sentence it sends are read by a user <em>beside every other intent's button</em>, exactly
    /// as the descriptions are weighed beside every other description — so they are written where
    /// the whole set is visible and not one at a time in an agent's own pass where nothing can be
    /// compared to anything.
    /// </para>
    /// </remarks>
    /// <param name="name">Bare lowercase word — becomes a C# attribute argument and a prompt ID.</param>
    /// <param name="description">What routes here, weighed by the classifier against every other intent's.</param>
    /// <param name="label">The quick-reply button text a user reads, if this call is also settling it.</param>
    /// <param name="defaultValue">The sentence pressing that button sends, as if the user had typed it.</param>
    public string DeclareIntent(string name, string description, string? label = null, string? defaultValue = null)
    {
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return "Nothing recorded: an intent must have a name — it becomes a C# attribute argument and a prompt ID.";

        if (string.Equals(cleanName, ReservedFallbackIntent, StringComparison.OrdinalIgnoreCase))
            return $"Nothing recorded: '{ReservedFallbackIntent}' is reserved. It is the intent the classifier "
                   + "falls back to when it cannot place a message and no agent may claim it. Call this again with a name from the domain.";

        if (draftStateService.Current?.Intents.Any(i =>
                string.Equals(i.Name, cleanName, StringComparison.OrdinalIgnoreCase)) == true)
            return $"Nothing recorded: '{cleanName}' is already an intent of this domain, written before today. "
                   + "Two agents answering the same intent is a startup failure. Name what is different about this one.";

        IntentDraft? existing = interviewState.Map.FirstOrDefault(i =>
            string.Equals(i.Name, cleanName, StringComparison.OrdinalIgnoreCase));

        bool revision = existing is not null;
        IntentDraft intent = existing ?? new IntentDraft { Origin = Provenance.Authored };

        intent.Name = cleanName;
        intent.Description = description?.Trim();

        // Left alone when the call omits them, so declaring an entry early and giving it a button
        // later is one entry revised twice rather than a button silently thrown away.
        if (!string.IsNullOrWhiteSpace(label))
            intent.Label = label.Trim();

        if (!string.IsNullOrWhiteSpace(defaultValue))
            intent.DefaultValue = defaultValue.Trim();

        if (!revision)
            interviewState.Map.Add(intent);

        List<string> complaints = [];

        if (!cleanName.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
            complaints.Add("But that is not a bare lowercase word and it becomes a C# attribute argument and a prompt ID. Call this again with one that is.");

        if (string.IsNullOrWhiteSpace(description))
            complaints.Add("It says nothing about what routes here, which is the sentence the classifier weighs against every other intent.");

        if (string.IsNullOrWhiteSpace(intent.Label) || string.IsNullOrWhiteSpace(intent.DefaultValue))
            complaints.Add("It has no button yet: the map is not settled until every entry carries the words a user reads and the sentence pressing them sends.");

        return (revision ? $"'{cleanName}' revised on the map." : $"'{cleanName}' is on the map, in position {interviewState.Map.Count}.")
               + (complaints.Count > 0 ? " " + string.Join(" ", complaints) : string.Empty);
    }

    /// <summary>
    /// Takes a kind of request off the map.
    /// </summary>
    public string DropIntent(string name)
    {
        int removed = interviewState.Map.RemoveAll(i =>
            string.Equals(i.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        return removed > 0
            ? $"'{name}' is off the map."
            : $"Nothing dropped: '{name}' is not on the map.";
    }

    /// <summary>
    /// Returns the domain map as it currently stands.
    /// </summary>
    /// <remarks>
    /// Read back whole for the same reason the toolkit is: two descriptions overlap or they do not;
    /// that is only visible side by side. It is the one defect no prose downstream repairs — the
    /// user meets it as the wrong agent answering.
    /// </remarks>
    public string GetDomainMap()
    {
        if (interviewState.Map.Count == 0)
            return "The map is empty: no kind of request has been named yet.";

        return "The domain map as it stands. The descriptions are weighed against each other by the classifier, "
               + "and the buttons are read side by side by a user:\n"
               + string.Join("\n", interviewState.Map.Select((i, n) =>
                   $"{n + 1}. {i.Name}: {i.Description ?? "(nothing said about what routes here)"}"
                   + $"\n    button: {i.Label ?? "(none)"} → \"{i.DefaultValue ?? "(nothing)"}\""));
    }

    /// <summary>
    /// Records the agent's Target section.
    /// </summary>
    /// <remarks>
    /// Overwrites whatever was there — a pass may call this more than once as the client's answer
    /// sharpens and the last call is the one that stands. The section is stored as written: its label
    /// is the framework's to add when the prompt is composed. The returned sentence tells the model whether the prose it just wrote fits this section's shape — never blocking,
    /// only informing, so the model can tighten a Target that ran long before moving on.
    /// </remarks>
    public string SetAgentTarget(string target)
    {
        interviewState.Agent.Target = target?.Trim();
        return Shaped("Target", target, 2, 4);
    }

    /// <summary>
    /// Records what a colleague reads before consulting this agent.
    /// </summary>
    /// <remarks>
    /// Same overwrite-and-report contract as <see cref="SetAgentTarget"/>. Written by the same pass and from the same scope, because it
    /// is that scope addressed to a different reader: a colleague deciding whether a question is this
    /// agent's. So it names a territory and never a list of what the agent can do — a caller handed an
    /// inventory rules questions out instead of asking them. Short by nature, which is why the shape
    /// it reports against is tighter than the Target's.
    /// </remarks>
    public string SetAgentTerritory(string territory)
    {
        interviewState.Agent.Territory = territory?.Trim();
        return Shaped("Territory", territory, 1, 3);
    }

    /// <summary>
    /// Records the agent's Personality section.
    /// </summary>
    /// <remarks>
    /// Same overwrite-and-report contract as <see cref="SetAgentTarget"/>. Only the <c>AgentPersonality</c> pass declares this tool,
    /// so it is the one place in the interview a voice can be written.
    /// </remarks>
    public string SetAgentPersonality(string personality)
    {
        interviewState.Agent.Personality = personality?.Trim();
        return Shaped("Personality", personality, 2, 3);
    }

    /// <summary>
    /// Records the agent's Instructions section.
    /// </summary>
    /// <remarks>
    /// Same overwrite-and-report contract as <see cref="SetAgentTarget"/>. Declared only from the <c>AgentInstructions</c> pass on, once
    /// the toolkit exists — Instructions speaks about the agent's tools, so nothing earlier may
    /// write it.
    /// <para>
    /// The tightest shape of the five is the one the client feels: what a tool does, needs and
    /// refuses belongs to that tool's own description, so a section running long is one holding a
    /// line per tool — a second authority on each subject and, stated back on screen, a wall of
    /// text where a sentence was owed.
    /// </para>
    /// </remarks>
    public string SetAgentInstructions(string instructions)
    {
        interviewState.Agent.Instructions = instructions?.Trim();
        return Shaped("Instructions", instructions, 2, 5);
    }

    /// <summary>
    /// Records the agent's Formatting section.
    /// </summary>
    /// <remarks>
    /// Same overwrite-and-report contract as <see cref="SetAgentTarget"/>. Declared only in the last pass, <c>AgentFormatting</c>, since
    /// everything else about the agent — the toolkit included — is settled by the time it runs.
    /// <para>
    /// The upper bound is wider than a plain presentation rule would need, on purpose: this section
    /// is also where a quick-reply payload or a per-tool card shape gets named exactly and a domain
    /// with several tools each earning one runs well past a handful of sentences to say so precisely
    /// — the alternative is a vaguer instruction the model has to improvise from at runtime, which is
    /// exactly what naming the payload here exists to avoid.
    /// </para>
    /// </remarks>
    public string SetAgentFormatting(string formatting)
    {
        interviewState.Agent.Formatting = formatting?.Trim();
        return Shaped("Formatting", formatting, 2, 10);
    }

    /// <summary>
    /// Opens a tool, or revises the description and the approval requirement of one already open.
    /// </summary>
    /// <remarks>
    /// Revising rewrites the description and the approval but keeps the parameters. A tool's
    /// contract is settled in several turns (the name and what it does come out of one answer,
    /// its inputs out of the next), so re-declaring it to
    /// sharpen the description must not silently empty it.
    /// </remarks>
    public string DeclareTool(string name, string description, bool requiresApproval)
    {
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return "No tool recorded: a tool must have a name, because the name is what pairs it with its C# method.";

        ToolDraft? existing = Find(cleanName);
        bool revision = existing is not null;

        ToolDraft tool = existing ?? new ToolDraft { Name = cleanName, Origin = Provenance.Authored };
        tool.Description = description?.Trim();
        tool.RequiresExecutionApproval = requiresApproval;

        if (!revision)
            interviewState.Agent.Tools.Add(tool);

        // Reported, never rewritten: the name is domain vocabulary and a silent correction leaves
        // Alembic telling the client one word while the configuration carries another.
        string complaint = IdentifierComplaint(cleanName, "tool name", pascalCase: true);

        return (revision ? $"'{cleanName}' revised." : $"'{cleanName}' declared.")
               + (complaint.Length > 0 ? " " + complaint : string.Empty)
               + (string.IsNullOrWhiteSpace(description)
                   ? " It has no description and the description is what the model reads when it decides whether to call this tool at all."
                   : string.Empty);
    }

    /// <summary>
    /// Adds a parameter to a tool, or revises one already there.
    /// </summary>
    /// <remarks>
    /// Revision is by name and in place, so the declaration order survives — which matters, because
    /// that order becomes the C# method's parameter order and C# cannot declare a required
    /// parameter after an optional one.
    /// </remarks>
    /// <param name="toolName">The already-declared tool this parameter belongs to.</param>
    /// <param name="name">camelCase — becomes the C# method's parameter name, matched by name not position.</param>
    /// <param name="description">What the model reads when deciding what to pass here.</param>
    /// <param name="scope">
    /// <c>"context"</c> if Morgana resolves it from the session, <c>"request"</c> if the agent must
    /// obtain it in conversation.
    /// </param>
    /// <param name="required">Whether the call fails without it — required parameters must precede optional ones and only a request parameter may be optional.</param>
    /// <param name="shared">Whether a resolved context value is published for other agents to hydrate from; only a <c>"context"</c> parameter may be shared.</param>
    public string SetToolParameter(string toolName, string name, string description, string scope, bool required, bool shared)
    {
        if (Find(toolName) is not { } tool)
            return $"No parameter recorded: no tool named '{toolName}' has been declared yet.";

        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return "No parameter recorded: a parameter must have a name, because the adapter pairs it with the C# method's parameter by name and not by position.";

        string? resolvedScope = ResolveScope(scope);

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

        List<string> complaints = [];

        string identifier = IdentifierComplaint(cleanName, "parameter name", pascalCase: false);
        if (identifier.Length > 0)
            complaints.Add(identifier);

        complaints.AddRange(ScopeComplaints(resolvedScope, required, shared));

        // The order is the signature, so an optional parameter followed by a required one is not a
        // preference: C# could not declare the signature.
        int firstOptional = tool.Parameters.FindIndex(p => !p.Required);
        if (firstOptional >= 0 && tool.Parameters.Skip(firstOptional).Any(p => p.Required))
            complaints.Add("A required parameter now sits after an optional one, which C# cannot declare. Reorder them by dropping and re-adding, or make the earlier one required.");

        return $"'{cleanName}' recorded on {tool.Name}."
               + (complaints.Count > 0 ? " " + string.Join(" ", complaints) : string.Empty);
    }

    /// <summary>
    /// Reads a scope the model wrote: one of the two scopes, the text as said where it is neither, or <c>null</c> where it is empty.
    /// </summary>
    /// <remarks>
    /// Internal because <see cref="CoherenceApplyTools"/> writes parameters under the same rules.
    /// </remarks>
    internal static string? ResolveScope(string? scope)
    {
        // Anything but the two scopes is kept as said, so that the complaint and validation both name it.
        string cleanScope = (scope ?? string.Empty).Trim().ToLowerInvariant();

        return cleanScope switch
        {
            ContextScope => ContextScope,
            RequestScope => RequestScope,
            "" => null,
            _ => cleanScope
        };
    }

    /// <summary>
    /// Says what Morgana refuses at startup in a parameter's scope, whether it is shared and whether it is optional.
    /// </summary>
    /// <remarks>
    /// Internal because <see cref="CoherenceApplyTools"/> writes parameters under the same rules.
    /// </remarks>
    /// <param name="scope">The scope as <see cref="ResolveScope"/> read it.</param>
    /// <param name="required">Whether the call fails without the parameter.</param>
    /// <param name="shared">Whether the resolved value is published for other agents.</param>
    internal static List<string> ScopeComplaints(string? scope, bool required, bool shared)
    {
        List<string> complaints = [];

        if (scope is not ContextScope and not RequestScope)
            complaints.Add($"'{scope}' is not a scope: every parameter declares '{ContextScope}' or '{RequestScope}'.");

        if (shared && scope == RequestScope)
            complaints.Add("A request parameter cannot be shared: only what the context holds is shared and Morgana refuses the combination at startup.");

        if (!required && scope == ContextScope)
            complaints.Add("A context parameter cannot be optional: a context value that nobody holds stops the tool, so a default would never be used and Morgana refuses it at startup.");

        return complaints;
    }

    /// <summary>
    /// Removes a parameter from a tool.
    /// </summary>
    public string DropToolParameter(string toolName, string parameterName)
    {
        if (Find(toolName) is not { } tool)
            return $"Nothing dropped: no tool named '{toolName}' has been declared.";

        int removed = tool.Parameters.RemoveAll(p =>
            string.Equals(p.Name, parameterName?.Trim(), StringComparison.Ordinal));

        return removed > 0
            ? $"'{parameterName}' dropped from {tool.Name}."
            : $"Nothing dropped: {tool.Name} has no parameter named '{parameterName}'.";
    }

    /// <summary>
    /// Adds a field to what a tool hands back, or revises one already there.
    /// </summary>
    /// <remarks>
    /// Revision is by name and in place. The field becomes a property of the record the tool's method
    /// returns, so the name is the property's with its first letter lowered and the type is the
    /// property's exactly as C# writes it. The field named <c>error</c> is the failure.
    /// </remarks>
    /// <param name="toolName">The already-declared tool whose result this field belongs to.</param>
    /// <param name="name">camelCase as the model reads it — the record's property is this name with its first letter upper-cased.</param>
    /// <param name="description">What the model learns from this field of the result.</param>
    /// <param name="type">The C# type of the record's property, such as <c>string</c>, <c>decimal</c> or <c>List&lt;string&gt;</c>; <c>string</c> for the failure.</param>
    public string SetToolReturn(string toolName, string name, string description, string type)
    {
        if (Find(toolName) is not { } tool)
            return $"No field recorded: no tool named '{toolName}' has been declared yet.";

        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return "No field recorded: a field must have a name, because the record's property is paired with it by name.";

        string cleanType = (type ?? string.Empty).Trim();
        ToolReturnDraft field = new()
        {
            Name = cleanName,
            Description = (description ?? string.Empty).Trim(),
            Type = cleanType.Length == 0 ? null : cleanType
        };

        int index = tool.Returns.FindIndex(r => string.Equals(r.Name, cleanName, StringComparison.Ordinal));
        if (index >= 0)
            tool.Returns[index] = field;
        else
            tool.Returns.Add(field);

        List<string> complaints = [];

        string identifier = IdentifierComplaint(cleanName, "field name", pascalCase: false);
        if (identifier.Length > 0)
            complaints.Add(identifier);

        // Startup reads the record back through camelCase, so a name that changes on the way out and
        // back would be declared here and never found there.
        string property = CodeEmitService.PropertyName(cleanName);
        string converted = JsonNamingPolicy.CamelCase.ConvertName(property);
        if (!string.Equals(converted, cleanName, StringComparison.Ordinal))
            complaints.Add($"'{cleanName}' cannot come back from C# under that name: the record's property is '{property}', which the agent's model reads as '{converted}'. Declare it as '{converted}'.");

        if (cleanType.Length == 0)
            complaints.Add("It has no type: every field declares the C# type of its property, such as string, decimal or List<string>.");
        else if (!DraftValidationService.ParsesAsType(cleanType))
            complaints.Add($"'{cleanType}' is not a C# type as a record property would write it. Call again with one that is.");

        if (string.Equals(cleanName, Constants.Workflows.FailureField, StringComparison.Ordinal) && cleanType.Length > 0 && cleanType.TrimEnd('?') != "string")
            complaints.Add($"'{cleanName}' is the failure of {tool.Name} and holds why the call failed, so its type is string.");

        return $"'{cleanName}' recorded on what {tool.Name} hands back."
               + (complaints.Count > 0 ? " " + string.Join(" ", complaints) : string.Empty);
    }

    /// <summary>
    /// Removes a field from what a tool hands back.
    /// </summary>
    public string DropToolReturn(string toolName, string name)
    {
        if (Find(toolName) is not { } tool)
            return $"Nothing dropped: no tool named '{toolName}' has been declared.";

        int removed = tool.Returns.RemoveAll(r =>
            string.Equals(r.Name, name?.Trim(), StringComparison.Ordinal));

        return removed > 0
            ? $"'{name}' dropped from what {tool.Name} hands back."
            : $"Nothing dropped: {tool.Name} hands back no field named '{name}'.";
    }

    /// <summary>
    /// Removes a tool and everything on it.
    /// </summary>
    /// <remarks>
    /// The workflow steps that still name it are left as they are and reported, so the pass that
    /// owns workflows repairs them rather than a tool being dropped behind their back.
    /// </remarks>
    public string DropTool(string toolName)
    {
        string cleanName = toolName?.Trim() ?? string.Empty;

        int removed = interviewState.Agent.Tools.RemoveAll(t =>
            string.Equals(t.Name, cleanName, StringComparison.Ordinal));

        if (removed == 0)
            return $"Nothing dropped: no tool named '{toolName}' has been declared.";

        List<string> orphaned =
        [.. interviewState.Agent.Workflows.SelectMany(workflow => workflow.Steps
                .Where(step => step.Tools.Contains(cleanName, StringComparer.Ordinal))
                .Select(step => $"'{cleanName}' is still named by step '{step.Name}' of workflow '{workflow.Name}', which is left without it."))];

        return $"'{toolName}' dropped, with its parameters and what it hands back."
               + (orphaned.Count > 0 ? " " + string.Join(" ", orphaned) : string.Empty);
    }

    /// <summary>
    /// Returns the toolkit as it currently stands.
    /// </summary>
    public string GetToolkit()
    {
        if (interviewState.Agent.Tools.Count == 0)
            return "This agent declares no tools yet. That is a legal end interviewState — an agent whose tools "
                   + "all arrive from an MCP server declares none here — but it must be a conclusion you reached by asking.\n\n"
                   + DescribeWorkflows(interviewState.Agent.Workflows);

        IEnumerable<string> rendered = interviewState.Agent.Tools.Select(t =>
            $"- {t.Name}{(t.RequiresExecutionApproval ? " (waits for the user's approval)" : string.Empty)}: {t.Description ?? "(no description)"}"
            + (t.Parameters.Count == 0
                ? "\n    (takes nothing)"
                : string.Concat(t.Parameters.Select(p =>
                    $"\n    {p.Name} [{p.Scope ?? "no scope yet"}"
                    + (p.Required ? "" : ", optional")
                    + (p.Shared ? ", shared" : "")
                    + $"]: {p.Description ?? "(no description)"}")))
            + (t.Returns.Count == 0
                ? "\n    (hands back nothing declared yet)"
                : string.Concat(t.Returns.Select(r =>
                    $"\n    returns {r.Name} ({r.Type ?? "no type yet"}){(r.Name == Constants.Workflows.FailureField ? " [the failure]" : string.Empty)}: "
                    + (string.IsNullOrWhiteSpace(r.Description) ? "(no description)" : r.Description)))));

        return "The toolkit as it stands:\n" + string.Join("\n", rendered)
               + "\n\n" + DescribeWorkflows(interviewState.Agent.Workflows);
    }

    /// <summary>
    /// Opens a workflow, or revises the description of one already open.
    /// </summary>
    /// <remarks>
    /// Revising rewrites the description and keeps the steps: a workflow is settled in several turns
    /// (what it achieves comes out of one answer, its steps out of the next), so re-declaring it to
    /// sharpen the description must not silently empty it.
    /// </remarks>
    public string DeclareWorkflow(string name, string description)
    {
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return "No workflow recorded: a workflow must have a name, because the model starts it by that name.";

        // The name is what the model passes to LaunchWorkflow, so a workflow kept under one that has
        // to change would survive the corrected call as a second workflow.
        string complaint = IdentifierComplaint(cleanName, "workflow name", pascalCase: true);

        if (complaint.Length > 0)
            return "No workflow recorded. " + complaint;

        WorkflowDraft? existing = FindWorkflow(cleanName);
        WorkflowDraft workflow = existing ?? new WorkflowDraft { Name = cleanName, Origin = Provenance.Authored };
        workflow.Description = description?.Trim();

        if (existing is null)
            interviewState.Agent.Workflows.Add(workflow);

        return (existing is not null ? $"'{cleanName}' revised." : $"'{cleanName}' declared.")
               + (string.IsNullOrWhiteSpace(description)
                   ? " It has no description and the description is what the model reads when it decides whether to start this workflow at all."
                   : string.Empty);
    }

    /// <summary>
    /// Adds a step at the end of a workflow, or revises one already there by name and in place.
    /// </summary>
    /// <remarks>
    /// Revision keeps the step's place, because the order of the steps is the order the procedure
    /// was described in and the first one is where it starts. A link to a step that is not declared
    /// yet is legal: steps arrive one by one and <see cref="GetFindings"/> reports a link left dangling.
    /// </remarks>
    /// <param name="workflow">The already-declared workflow this step belongs to.</param>
    /// <param name="name">PascalCase and never <c>End</c>, which is the reserved target that closes a workflow.</param>
    /// <param name="tools">The tools offered at this step, by their exact names.</param>
    /// <param name="next">Tool name to the step its call leads to when it succeeds, or <c>End</c>.</param>
    /// <param name="onFailure">Tool name to the step its call leads to when it fails; a tool left out ends the workflow.</param>
    /// <param name="arguments">Parameter name to <c>Step.field</c>, the earlier result the framework fills it from.</param>
    public string SetWorkflowStep(
        string workflow,
        string name,
        string[] tools,
        Dictionary<string, string> next,
        Dictionary<string, string>? onFailure = null,
        Dictionary<string, string>? arguments = null)
    {
        if (FindWorkflow(workflow) is not { } owner)
            return $"No step recorded: no workflow named '{workflow}' has been declared yet.";

        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return "No step recorded: a step must have a name.";

        List<string> stepTools = [.. (tools ?? []).Select(tool => tool?.Trim() ?? string.Empty)
                                                  .Where(tool => tool.Length > 0)
                                                  .Distinct(StringComparer.Ordinal)];
        List<string> complaints = [];

        string identifierComplaint = IdentifierComplaint(cleanName, "step name", pascalCase: true);

        if (identifierComplaint.Length > 0)
            complaints.Add(identifierComplaint);

        if (string.Equals(cleanName, Constants.Workflows.End, StringComparison.Ordinal))
            complaints.Add($"But '{Constants.Workflows.End}' is the reserved target that closes a workflow and no step may bear it.");

        // An agent that acquires tools from an MCP server names tools nothing here can see, so only
        // an agent with none of those can have a tool judged absent on the spot.
        if (interviewState.Agent.Code.MCPServers.Count == 0)
            foreach (string tool in stepTools.Where(tool => Find(tool) is null))
                complaints.Add($"But this agent declares no tool named '{tool}'.");

        if (stepTools.Count == 0)
            complaints.Add("But a step offers at least one tool.");

        Dictionary<string, string> successLinks = Cleaned(next);
        Dictionary<string, string> failureLinks = Cleaned(onFailure);
        Dictionary<string, string> boundValues = Cleaned(arguments);

        foreach ((string linkKind, Dictionary<string, string> links) in new[] { ("next", successLinks), ("onFailure", failureLinks) })
            foreach (string tool in links.Keys.Where(tool => !stepTools.Contains(tool, StringComparer.Ordinal)))
                complaints.Add($"But '{tool}' in {linkKind} is not a tool of this step.");

        foreach ((string parameter, string source) in boundValues)
        {
            string[] parts = source.Split('.');

            if (parts.Length != 2 || parts.Any(part => part.Length == 0))
                complaints.Add($"But '{source}', the source of '{parameter}', is not of the form Step.field.");
        }

        if (complaints.Count > 0)
            return "No step recorded. " + string.Join(" ", complaints) + " Call again with the step corrected.";

        WorkflowStepDraft? existing = owner.Steps.FirstOrDefault(step => string.Equals(step.Name, cleanName, StringComparison.Ordinal));
        WorkflowStepDraft recorded = existing ?? new WorkflowStepDraft { Name = cleanName };

        recorded.Tools = stepTools;
        recorded.Next = successLinks;
        recorded.OnFailure = failureLinks;
        recorded.Arguments = boundValues;

        if (existing is null)
            owner.Steps.Add(recorded);

        return existing is not null
            ? $"Step '{cleanName}' of '{owner.Name}' revised."
            : $"Step '{cleanName}' added to '{owner.Name}' as step {owner.Steps.Count}.";
    }

    /// <summary>
    /// Removes a step from a workflow.
    /// </summary>
    public string DropWorkflowStep(string workflow, string step)
    {
        if (FindWorkflow(workflow) is not { } owner)
            return $"Nothing dropped: no workflow named '{workflow}' has been declared.";

        int removed = owner.Steps.RemoveAll(candidate =>
            string.Equals(candidate.Name, step?.Trim(), StringComparison.Ordinal));

        return removed > 0
            ? $"Step '{step}' dropped from '{owner.Name}'."
            : $"Nothing dropped: '{owner.Name}' has no step named '{step}'.";
    }

    /// <summary>
    /// Removes a workflow and every step of it.
    /// </summary>
    public string DropWorkflow(string name)
    {
        int removed = interviewState.Agent.Workflows.RemoveAll(workflow =>
            string.Equals(workflow.Name, name?.Trim(), StringComparison.Ordinal));

        return removed > 0
            ? $"'{name}' dropped, with its steps."
            : $"Nothing dropped: no workflow named '{name}' has been declared.";
    }

    /// <summary>
    /// Returns the workflows as they currently stand.
    /// </summary>
    public string GetWorkflows() => DescribeWorkflows(interviewState.Agent.Workflows);

    /// <summary>
    /// Renders workflows as readable text: one block each, its steps in order with the tools, where
    /// each call leads, the failures and the values bound from earlier steps.
    /// </summary>
    /// <remarks>
    /// Internal because the coherence passes read an agent's workflows in the same words the
    /// interview does, so the prose they judge is set against the procedure it would restate.
    /// </remarks>
    internal static string DescribeWorkflows(IReadOnlyList<WorkflowDraft> workflows)
    {
        if (workflows.Count == 0)
            return "This agent has no workflow.";

        return "The workflows as they stand:\n\n"
               + string.Join("\n\n", workflows.Select(workflow =>
                   $"Workflow {workflow.Name ?? "(unnamed)"}: {workflow.Description ?? "(no description)"}"
                   + (workflow.Steps.Count == 0
                       ? "\n  (no step yet)"
                       : string.Concat(workflow.Steps.Select((step, position) =>
                           $"\n  {position + 1}. {step.Name ?? "(unnamed)"} — tools: "
                           + (step.Tools.Count == 0 ? "(none yet)" : string.Join(", ", step.Tools))
                           + Links("on success", step.Next)
                           + Links("on failure", step.OnFailure)
                           + (step.Arguments.Count == 0
                               ? string.Empty
                               : "\n     binds: " + string.Join(", ", step.Arguments.Select(pair => $"{pair.Key} from {pair.Value}"))))))));
    }

    /// <summary>
    /// One line of where a step's tools lead, or nothing where it declares none.
    /// </summary>
    private static string Links(string label, Dictionary<string, string> links) =>
        links.Count == 0
            ? string.Empty
            : $"\n     {label}: " + string.Join(", ", links.Select(pair => $"{pair.Key} leads to {pair.Value}"));

    /// <summary>
    /// A link table as the model sent it, with names and targets trimmed and empty entries gone.
    /// </summary>
    private static Dictionary<string, string> Cleaned(Dictionary<string, string>? links) =>
        (links ?? [])
            .Select(pair => (Key: pair.Key?.Trim() ?? string.Empty, Value: pair.Value?.Trim() ?? string.Empty))
            .Where(pair => pair.Key.Length > 0 && pair.Value.Length > 0)
            .GroupBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal);

    /// <summary>
    /// Finds a declared workflow by exact name, ordinal because the framework looks it up that way.
    /// </summary>
    private WorkflowDraft? FindWorkflow(string? workflowName) =>
        interviewState.Agent.Workflows.FirstOrDefault(workflow =>
            string.Equals(workflow.Name, workflowName?.Trim(), StringComparison.Ordinal));

    /// <summary>
    /// Returns what earlier passes settled about this agent.
    /// </summary>
    /// <remarks>
    /// Each pass is a fresh agent with a fresh session, so nothing of the previous conversation
    /// carries over — deliberately, because a toolkit pass that still has the whole functional
    /// interview in its context spends it re-litigating decisions already taken. What must carry
    /// over is the configuration and the configuration is exactly what this returns.
    /// </remarks>
    public string GetAgentSoFar()
    {
        if (string.IsNullOrWhiteSpace(interviewState.Intent.Name))
            return "Nothing settled yet: this agent has no intent.";

        List<string> sections =
        [
            $"Intent '{interviewState.Intent.Name}': {interviewState.Intent.Description}",
            $"Opening sentence a user would send: {interviewState.Intent.DefaultValue}",
            Records.Prompt.Labeled(Constants.SectionLabels.Target, interviewState.Agent.Target ?? "(no target)"),
            Records.Prompt.Labeled(Constants.SectionLabels.Personality, interviewState.Agent.Personality ?? "(no personality)")
        ];

        if (!string.IsNullOrWhiteSpace(interviewState.Agent.Instructions))
            sections.Add(Records.Prompt.Labeled(Constants.SectionLabels.Instructions, interviewState.Agent.Instructions));

        if (!string.IsNullOrWhiteSpace(interviewState.Agent.Formatting))
            sections.Add(Records.Prompt.Labeled(Constants.SectionLabels.Formatting, interviewState.Agent.Formatting));

        if (interviewState.Agent.Workflows.Count > 0)
            sections.Add(DescribeWorkflows(interviewState.Agent.Workflows));

        return "Settled in the earlier passes and not yours to reopen:\n\n"
               + string.Join("\n\n", sections);
    }

    /// <summary>
    /// Offers words for the agent's voice, to be picked from freely.
    /// </summary>
    /// <remarks>
    /// Not buttons and the tool is separate for the same reason the shape is: a button is one whole
    /// answer and these are taken several at a time, none of them an answer on its own. The words
    /// come from the model because they have to be about this domain and this agent and to sit
    /// inside Morgana's own voice, which it has read and a template has not.
    /// </remarks>
    public string SetTraits(string traits)
    {
        try
        {
            List<string>? parsed = JsonSerializer.Deserialize<List<string>>(
                traits, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            List<string> words = [.. (parsed ?? []).Select(w => w.Trim()).Where(w => w.Length > 0)];

            if (words.Count == 0)
                return "No words offered: the payload held none.";

            interviewState.PendingTraits.Clear();
            interviewState.PendingTraits.AddRange(words);

            return $"{words.Count} words will be drawn under your question, to be picked from freely. "
                   + "The text box stays open, so the answer may still come in the client's own words.";
        }
        catch (JsonException ex)
        {
            return $"No words offered: the payload is not a JSON array of words ({ex.Message}).";
        }
    }

    /// <summary>
    /// How much one agent, or the business itself, may hold on record before it has to be tidied.
    /// </summary>
    /// <remarks>
    /// Not a storage limit: every step opens holding what is known about the agent in hand, so a
    /// record that grows without end is a step reading forty sentences to ask one question. Reaching
    /// it is a sign two facts have become one fact said twice, which is the pass's own to settle.
    /// </remarks>
    private const int MemoryCeiling = 14;

    /// <summary>
    /// Where what is found out is kept: with the agent in hand, or with the business.
    /// </summary>
    /// <remarks>
    /// The map and the closing step both stand on the whole domain; the closing step stands past
    /// the end of the map holding an agent nobody will write to — so what is learned there is about
    /// the business or it is lost with that empty agent.
    /// </remarks>
    private List<KnownFact> Memory => interviewState.OnAnEntry
        ? interviewState.Agent.Known
        : (draftStateService.Current ?? new DomainDraft()).Learned;

    /// <summary>
    /// Writes down one thing the client has just said about how their work actually goes.
    /// </summary>
    /// <remarks>
    /// Every pass is a fresh session that reads what is written and nothing else, so what the client
    /// says about their trade is spent the moment the turn ends unless it is written down here. That
    /// is how a step three passes later comes to ask a shopkeeper what a system ought to be able to
    /// check: it never knew there was a shop. A fact about the agent in hand is kept with that agent
    /// and travels with it; a fact about the business itself is kept for the whole domain. Neither
    /// ever enters the domain — this is what the questions are made of, not what the agents say.
    /// </remarks>
    /// <param name="subject">What it is about, in one or two of the client's own words.</param>
    /// <param name="fact">The fact itself, one sentence, in their vocabulary.</param>
    /// <param name="corrects">
    /// What this puts right, where the client has just contradicted something on record: any part of
    /// the wrong sentence is enough to find it. A reading taken off their upload is exactly the kind
    /// of thing that gets corrected here and it must go, rather than sit under the truth.
    /// </param>
    public string NoteDomainFact(string subject, string fact, string? corrects = null)
    {
        string written = fact.Trim();
        string about = subject.Trim();

        if (written.Length == 0 || about.Length == 0)
            return "Nothing written down: a fact needs both a subject and a sentence.";

        List<KnownFact> kept = Memory;
        int dropped = corrects is { Length: > 0 } wrong ? Forget(kept, wrong) : 0;

        // What the client says stands over what Alembic read off their upload about the same
        // subject: a reading of somebody else's prose loses to the person whose shop it is.
        dropped += kept.RemoveAll(known =>
            known.Inferred && string.Equals(known.Subject, about, StringComparison.OrdinalIgnoreCase));

        if (kept.Any(known => string.Equals(known.Fact, written, StringComparison.OrdinalIgnoreCase)))
            return "That was already written down; nothing added.";

        if (kept.Count >= MemoryCeiling)
            return $"Nothing written down: {kept.Count} facts already stand here, which is as many as "
                   + "are worth carrying into a question. Two of them have become the same fact in "
                   + "different words — drop one with DropDomainFact, or say what this one corrects.";

        kept.Add(new KnownFact(about, written, Inferred: false));

        return $"Written down under '{about}'"
               + (dropped > 0 ? $"; {dropped} thing(s) that said otherwise are gone" : string.Empty)
               + $". {kept.Count} thing(s) now stand on record here.";
    }

    /// <summary>
    /// Takes something off the record that turned out not to be true of their work.
    /// </summary>
    /// <remarks>
    /// The one thing a memory owes whoever it is about. Alembic reads an uploaded configuration for
    /// what it says about the business and it can read it wrong; a client says something that was
    /// true last year. Left standing, either is worse than never having known: every step after this
    /// one opens holding it and none of them has any way to doubt it.
    /// </remarks>
    /// <param name="fact">Any part of the sentence to remove, enough to tell it from the others.</param>
    public string DropDomainFact(string fact)
    {
        string wrong = fact.Trim();

        if (wrong.Length == 0)
            return "Nothing dropped: the payload was empty.";

        int dropped = Forget(Memory, wrong);

        return dropped == 0
            ? "Nothing here says that; nothing dropped."
            : $"{dropped} thing(s) gone from the record. No step after this one will hold them.";
    }

    /// <summary>
    /// Hands back what is on record about one of the other agents of this domain.
    /// </summary>
    /// <remarks>
    /// A step opens holding what is known about its own agent and the business, with only the subjects
    /// the other agents keep — a domain of nine agents read whole would be forty sentences carried into
    /// every question, most of them about counters this step will never touch. This is how the rest
    /// is reached, when a subject listed there turns out to bear on the question in hand: whether the
    /// counter next door already takes deposits decides whether this one should.
    /// </remarks>
    /// <param name="intent">The agent's own intent name, as the opening message lists it.</param>
    public string RecallAgent(string intent)
    {
        string named = intent.Trim();
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // An agent of this domain is an entry of the map, written or still ahead — the same universe
        // the opening message lists its neighbours from. Resolved against the written agents alone,
        // this denied the existence of every agent the interview had not reached yet, which is most
        // of them on the first agent and all of them the client had just dictated.
        IntentDraft? entry = draft.Intents.Concat(interviewState.Map).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, named, StringComparison.OrdinalIgnoreCase));

        AgentDraft? agent = draft.Agents.FirstOrDefault(agent =>
            string.Equals(agent.ID, named, StringComparison.OrdinalIgnoreCase));

        if (entry is null && agent is null)
            return $"There is no agent called '{named}' in this domain. The opening message lists them by name.";

        // What the map says about an agent nobody has opened yet is the whole of what is known about
        // it. It is worth more than a refusal: the routing sentence the client dictated is the
        // only account of that counter anybody has.
        if (agent is null || agent.Known.Count == 0)
            return string.IsNullOrWhiteSpace(entry?.Description)
                ? $"Nothing is on record about how they work at '{named}'."
                : $"Nothing is on record yet about how they work at '{named}'. The map describes it as: {entry.Description}";

        return $"What is known about '{named}':\n"
               + string.Join("\n", agent.Known.Select(known =>
                   $"- {known.Subject}: {known.Fact}" + (known.Inferred ? " (read off their configuration, not said)" : string.Empty)));
    }

    /// <summary>
    /// Removes whatever on record carries the wrong sentence, by subject or by wording.
    /// </summary>
    private static int Forget(List<KnownFact> kept, string wrong) =>
        kept.RemoveAll(known =>
            known.Fact.Contains(wrong, StringComparison.OrdinalIgnoreCase)
            || wrong.Contains(known.Fact, StringComparison.OrdinalIgnoreCase)
            || string.Equals(known.Subject, wrong, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Says what the step about to be asked adds to the agent in hand, over its question.
    /// </summary>
    /// <remarks>
    /// A step that lands saying nothing but its question leaves the client to work out what this
    /// screen is for from the question alone, which is the one thing it cannot tell them. Held apart
    /// from the question rather than written above it in the same breath, because the screen draws
    /// the two in two different voices: what they are told and what they are asked are read at
    /// different moments and one block at one size is read as neither. It belongs to the turn a step
    /// lands on and to no confirmation or follow-up after it.
    /// </remarks>
    public string SetStepPlacing(string placing)
    {
        string written = placing.Trim();

        if (written.Length == 0)
            return "Nothing placed: the payload was empty.";

        interviewState.PendingPlacing = written;

        return "That will stand above the question, quieter than it and apart from it. It is the only "
               + "thing on the screen telling them what this step is for, so the question itself "
               + "need not say it again.";
    }

    /// <summary>
    /// Shows the client, word for word, the prose just written for their agent.
    /// </summary>
    /// <remarks>
    /// The turn that asks whether a section is right is the one turn where the client is approving
    /// exact words, so those words stand apart from the sentence introducing them. Run into one
    /// paragraph the two become a single stretch of prose in which nothing marks where Alembic stops
    /// speaking and the agent's own text begins. An approval given to that approves nothing in
    /// particular, which is the whole of what this interview is for.
    /// </remarks>
    /// <param name="written">The section's prose exactly as it now stands, with nothing added around it.</param>
    public string ShowWhatIsWritten(string written)
    {
        string prose = written.Trim();

        if (prose.Length == 0)
            return "Nothing shown: the payload was empty.";

        interviewState.PendingQuoted = prose;

        return "That will stand on its own above your question, exactly as you wrote it. Your own "
               + "sentence should introduce it and never contain it: they are approving these words, "
               + "so what they read has to be only these words.";
    }

    /// <summary>
    /// Puts a worked example in the answer box under the question about to be asked.
    /// </summary>
    /// <remarks>
    /// Called before every question that asks for something new, cut to the size of that question:
    /// what it teaches is register, length and level of detail, which is the whole of what a
    /// first-time client is missing and none of what a label naming the material would convey. An
    /// empty box under an open question teaches none of it and comes back as a word where a
    /// sentence was wanted — which is how a domain arrives thin and only shows its holes at the
    /// emit. Written by the pass rather than fixed in the UI, so it is an answer somebody in the
    /// client's own trade might have given rather than a stranger's business quoted at them.
    /// </remarks>
    public string SetExample(string example)
    {
        // The box holds it as the client's own text, so a pair of quotation marks around it is text
        // they would be sending. One at a single end is what a half-escaped payload leaves behind.
        string written = example.Trim().Trim('"').Trim();

        if (written.Length == 0)
            return "No example put in the box: the payload was empty.";

        interviewState.PendingExample = written;

        return "The example will stand in the answer box under your question, greyed and goes the "
               + "moment they answer. It is the only thing on the screen telling them how long an "
               + "answer is worth writing.";
    }

    /// <summary>
    /// Attaches to the question about to be asked the one button that answers it without adding anything.
    /// </summary>
    /// <remarks>
    /// An acknowledgement the client may take or leave: where the model is already right — everything
    /// stated back stands, nothing is missing, there is nothing the agent should be kept off — the
    /// turn settles with a press instead of a typed sentence and a refining exchange nobody learns
    /// anything from. Two plain strings rather than an array of buttons, because that answer is the
    /// only one Alembic knows the whole of; everything the client actually contributes is a sentence
    /// only they can write and they write it in the box, which never closes. A schema admitting a
    /// list would have the model compose a second button before anything could drop it, which costs
    /// the tokens whether or not it is ever drawn.
    /// The id is Alembic's: nothing downstream tells two buttons apart when there is only one and
    /// asking for it would be a third string with no reader.
    /// </remarks>
    public string SetChoice(string label, string value)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
            return "No button attached: it needs both a label to read and the answer it sends.";

        interviewState.PendingChoice = new QuickReply("agree", label.Trim(), value.Trim());

        return "The button will be drawn under your question. "
               + "The text box stays open, so the answer may still come in the client's own words.";
    }

    /// <summary>
    /// Returns every agent of the finished domain, whole, with the colleagues already declared.
    /// </summary>
    /// <remarks>
    /// The closing step runs with a fresh session like every other and unlike every other it is
    /// about the domain rather than about the agent in hand — there is none. So this is the whole of
    /// what it knows and it has to be the whole: an edge is a claim that one agent's question lands
    /// on another's books, which is only decidable with both agents' Targets, toolkits and
    /// boundaries in view at once.
    /// </remarks>
    public string GetDomainAgents()
    {
        List<AgentDraft> agents = [.. (draftStateService.Current?.Agents ?? []).Where(a => !string.IsNullOrWhiteSpace(a.ID))];

        if (agents.Count == 0)
            return "The domain holds no agent yet: there is nothing that could ask anything of anything.";

        IEnumerable<string> rendered = agents.Select(a =>
            $"- {a.ID}\n    what it is for: {AgentRows.Plain(a.Target) ?? "(nothing said)"}"
            + $"\n    what it answers for, in the words a colleague reads: {AgentRows.Plain(a.Territory) ?? "(nothing said)"}"
            + $"\n    what it can reach: {(a.Tools.Count > 0 ? string.Join(", ", a.Tools.Select(t => t.Name ?? "(unnamed)")) : "no tool of its own")}"
            + $"\n    how it goes about it: {AgentRows.Plain(a.Instructions) ?? "(nothing said)"}"
            + $"\n    colleagues it may already ask: {PeerNaming.Describe(a.Code.Consults)}");

        return "The domain as it stands, every agent whole:\n" + string.Join("\n", rendered);
    }

    /// <summary>
    /// Returns the colleagues declared this step, none of them in the domain yet.
    /// </summary>
    public string GetConsultations()
    {
        if (interviewState.Colleagues.Count == 0)
            return "Nothing declared yet this step. A domain where no agent needs a colleague is an ordinary domain, "
                   + "and settling the step with none is a legitimate answer.";

        return "Declared so far, waiting on the client's word:\n"
               + string.Join("\n", interviewState.Colleagues.Select(c =>
                   $"- {c.Asking} may ask {c.Asked}"
                   + (c.AskingTarget is null ? string.Empty : $" (and {c.Asking}'s Target was rewritten with it)")
                   + (c.AskedInstructions is null ? string.Empty : $" (and {c.Asked}'s own instructions were reconciled too)")));
    }

    /// <summary>
    /// Declares that one agent may put a question to another and reconciles the prose that would
    /// otherwise forbid it.
    /// </summary>
    /// <remarks>
    /// The reconciled prose is a parameter and not an afterthought, because the edge without it is
    /// the characteristic defect this step exists against: an agent offered a colleague as a
    /// function while its own prose says the subject belongs to another bench and to say so plainly
    /// reads two contradictory orders and obeys the imperative one. What the prose must NOT do is
    /// restate the framework's own rules about consulting — when to ask, how briefly, that the answer
    /// is data — which are already on the function the agent will see.
    /// <para>
    /// Chained consultation is reported rather than refused: the framework denies a colleague its own
    /// peer functions while it is answering, so an edge whose far end asks a third agent is legal,
    /// simply narrower than it looks. The model is told exactly that, in the answer, so it can
    /// say it to the client instead of promising a reach the domain does not have.
    /// </para>
    /// </remarks>
    public string DeclareConsultation(
        string asking,
        string asked,
        string askingInstructions,
        string? askedInstructions = null,
        string? askingTarget = null)
    {
        string from = (asking ?? string.Empty).Trim();
        string to = (asked ?? string.Empty).Trim();

        if (from.Length == 0 || to.Length == 0)
            return "Nothing declared: an edge needs both the agent that asks and the colleague it asks.";

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return $"Nothing declared: '{from}' cannot consult itself. The startup registry refuses that pairing outright.";

        List<AgentDraft> agents = [.. draftStateService.Current?.Agents ?? []];

        if (!agents.Any(a => string.Equals(a.ID, from, StringComparison.OrdinalIgnoreCase)))
            return $"Nothing declared: no agent of this domain answers '{from}'.";

        if (!agents.Any(a => string.Equals(a.ID, to, StringComparison.OrdinalIgnoreCase)))
            return $"Nothing declared: no agent of this domain answers '{to}'.";

        if (string.IsNullOrWhiteSpace(askingInstructions))
            return $"Nothing declared: '{from}' needs its Instructions rewritten in the same call. An agent handed a "
                   + "colleague while its own prose still says the subject belongs elsewhere and to stop there is being "
                   + "given two orders and the flat one wins.";

        ConsultationDraft? existing = interviewState.Colleagues.FirstOrDefault(c =>
            string.Equals(c.Asking, from, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Asked, to, StringComparison.OrdinalIgnoreCase));

        ConsultationDraft edge = existing ?? new ConsultationDraft { Asking = from, Asked = to };

        edge.AskingInstructions = askingInstructions?.Trim()!;
        edge.AskedInstructions = string.IsNullOrWhiteSpace(askedInstructions)
            ? null
            : askedInstructions?.Trim();

        // The boundary is as often in the Target as in the Instructions, since that is where a boundary
        // belongs; one left refusing the colleague's subject goes on being read every turn.
        edge.AskingTarget = string.IsNullOrWhiteSpace(askingTarget)
            ? null
            : askingTarget?.Trim();

        if (existing is null)
            interviewState.Colleagues.Add(edge);

        // An agent asks with two hands and its colleague answers with none: while it is serving a
        // consultation, the framework refuses it its own peer functions. So an edge onto an agent
        // that itself asks somebody is not an error and not a reach either.
        bool secondHop = interviewState.Colleagues.Any(c =>
                             string.Equals(c.Asking, to, StringComparison.OrdinalIgnoreCase))
                         || agents.Any(a => string.Equals(a.ID, to, StringComparison.OrdinalIgnoreCase)
                                            && a.Code.Consults.Count > 0);

        return (existing is null ? $"'{from}' may now ask '{to}'." : $"The edge from '{from}' to '{to}' was revised.")
               + $" Its Instructions were rewritten with it{(edge.AskingTarget is null ? string.Empty : ", its Target too")}"
               + $"{(edge.AskedInstructions is null ? string.Empty : $" and '{to}'s Instructions as well")}."
               + (edge.AskingTarget is null
                   ? " If the sentence that turns this subject away is in its Target instead, send that rewritten too: "
                     + "the one left standing is read every turn."
                   : string.Empty)
               + (secondHop
                   ? $" Note that '{to}' consults a colleague of its own: while it is answering '{from}' the framework "
                     + "withholds its peer functions, so whatever it would have asked for is not part of this answer. Say "
                     + "that plainly if the client is expecting the far end."
                   : string.Empty);
    }

    /// <summary>
    /// Takes back an edge declared this step.
    /// </summary>
    /// <remarks>
    /// The Instructions that came with it go too. They were written to admit a colleague that is no
    /// longer there and leaving them in the domain would be the mirror of the defect the edge
    /// exists against: prose licensing a question the agent has no function to ask.
    /// </remarks>
    public string DropConsultation(string asking, string asked)
    {
        int removed = interviewState.Colleagues.RemoveAll(c =>
            string.Equals(c.Asking, asking?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Asked, asked?.Trim(), StringComparison.OrdinalIgnoreCase));

        return removed > 0
            ? $"Dropped: '{asking}' will not ask '{asked}' and the prose declared with it is dropped too."
            : $"Nothing dropped: no edge from '{asking}' to '{asked}' was declared this step.";
    }

    /// <summary>
    /// Returns the intents already in the domain.
    /// </summary>
    /// <remarks>
    /// Two sources and both are load-bearing. What the domain already holds arrived from earlier
    /// sittings or an uploaded configuration; what the map still holds is what this interview has
    /// promised to write next. A description only has to be told apart from both.
    /// </remarks>
    public string GetExistingIntents()
    {
        IEnumerable<string> written = (draftStateService.Current?.Intents ?? [])
            .Where(i => !string.Equals(i.Name, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase))
            .Select(i => $"- {i.Name}: {i.Description} (already in the domain)");

        IEnumerable<string> planned = interviewState.Map
            .Where(i => !ReferenceEquals(i, interviewState.Intent))
            .Select(i => $"- {i.Name}: {i.Description} (on the map, not written yet)");

        List<string> all = [.. written, .. planned];

        return all.Count == 0
            ? "Nothing else claims a route: this is the only intent and nothing can collide with it."
            : "The descriptions the classifier weighs this one against:\n" + string.Join("\n", all);
    }

    /// <summary>
    /// True when some OTHER agent of the draft declares it may consult this one — the half of the
    /// consultation relation an agent cannot see from its own <c>Consults</c> list.
    /// </summary>
    private bool IsConsultedByOthers(AgentDraft agent)
        => (draftStateService.Current?.Agents ?? []).Any(other =>
               other.ID != agent.ID &&
               other.Code.Consults.Any(colleague =>
                   colleague.Instance is null
                   && string.Equals(colleague.Intent, agent.ID, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Returns the prompt this agent's model will really read.
    /// </summary>
    public async Task<string> GetComposedPrompt()
    {
        if (string.IsNullOrWhiteSpace(interviewState.Agent.Target))
            return "Nothing to compose yet: the agent has no target.";

        AgentRecap recap = await recapService.ComposeAsync(
            interviewState.Agent,
            IsConsultedByOthers(interviewState.Agent));

        // Morgana's half is the same eleven thousand words for every agent of every domain and it is
        // already in this pass's own context once it has been read once. Sending it again on the
        // second reading — which the passes that hunt a sentence repeating hers are told to take —
        // doubles the request for nothing: what changed between the two is the agent's own prose and
        // only that. Whole the first time, because the comparison needs her words in front of it.
        int domain = framework ? recap.SystemPrompt.LastIndexOf(Constants.SectionLabels.Target, StringComparison.Ordinal) : -1;

        framework = true;

        return domain < 0
            ? "This is the whole of what this agent's model will read:\n\n" + recap.SystemPrompt
            : "Morgana's own layer above this one has not changed since you read it. This is the part "
              + "that is yours, as her composer lays it out:\n\n" + recap.SystemPrompt[domain..];
    }

    /// <summary>
    /// Returns the card this agent will present to whoever might consult it.
    /// </summary>
    /// <remarks>
    /// The last reading of a territory before it is published: a Morgana carries this card on the
    /// A2A endpoint of every agent it holds; a colleague weighing a question reads its
    /// description and nothing else. A sentence that reads as a list of functions, or one that never
    /// left the routing phrase the classifier uses, is visible here and nowhere else in the
    /// interview.
    /// </remarks>
    public string GetAgentCard()
    {
        return CardProjection.Render(interviewState.Intent, interviewState.Agent);
    }

    /// <summary>
    /// Rewrites the routing description of the intent in hand, strengthened by what the territory
    /// settled.
    /// </summary>
    /// <remarks>
    /// The classifier reads this description against every other one, so it is the map's to write
    /// and the map writes it before any agent exists. Once an agent has stated the competence it is
    /// the one to answer for, the same subject is known in sharper words than the map could reach;
    /// the routing that lands a user here can be said with them. Only this entry's own
    /// description: every other one is settled and reading them back is what keeps this one distinct.
    /// </remarks>
    public string SetIntentDescription(string description)
    {
        string written = (description ?? string.Empty).Trim();

        if (written.Length == 0)
            return "Nothing changed: an intent with no description is one the classifier cannot route to.";

        interviewState.Intent.Description = written;

        return $"'{interviewState.Intent.Name}' now routes on: {written}";
    }

    /// <summary>
    /// Returns everything wrong with this agent that is decidable without a model.
    /// </summary>
    /// <remarks>
    /// Checked against a probe domain — what the client already has, plus the agent under
    /// construction — because half of these rules are relational: an intent nothing routes to and a
    /// name colliding with a framework prompt are both invisible when an agent is examined alone.
    /// Findings about the client's other agents are filtered out: they are real, but they are not
    /// this pass's business and Alembic cannot fix them from here.
    /// </remarks>
    public string GetFindings()
    {
        if (string.IsNullOrWhiteSpace(interviewState.Intent.Name))
            return "Nothing to check yet: the intent has no name.";

        DomainDraft existing = draftStateService.Current ?? new DomainDraft();

        DomainDraft probe = new DomainDraft
        {
            Intents = [.. existing.Intents, interviewState.Intent],
            Agents = [.. existing.Agents, interviewState.Agent]
        };

        string mine = interviewState.Intent.Name!;

        List<ValidationFinding> findings =
            [.. draftValidationService.Validate(probe)
                .Where(f => f.Where.Contains($"'{mine}'", StringComparison.OrdinalIgnoreCase)
                            || f.Where.StartsWith($"{mine}.", StringComparison.OrdinalIgnoreCase)
                            || f.Where == "domain")];

        return findings.Count == 0
            ? "Nothing to report: every deterministic check passes for this agent."
            : string.Join("\n", findings.Select(f => $"[{f.Severity}] {f.Where}: {f.Message} — {f.Because}"));
    }

    /// <summary>
    /// Declares the pass settled.
    /// </summary>
    /// <remarks>
    /// Believed only as far as the state machine can confirm it. Which fields are set is a fact
    /// and facts are not a model's to assert.
    /// </remarks>
    public string SetPassCompleted()
    {
        // Correcting, every section is written already, so each pass could settle the moment it
        // opened and the client would be walked through an edit that asked them nothing at all —
        // which is how a client who came to change a voice reached the end with the voice they
        // came to change. A pass may still settle on their first word; the doctrine's own fast
        // path stands: what it may not do is settle before they have said one.
        if (interviewState.Revision is not null && interviewState.Exchanges == interviewState.PassOpenedAt)
            return "Not completed: this section is reopened and the client has not said a word about it yet. "
                   + "State what it says today, attach the choice that agrees with it and settle it on their answer.";

        IReadOnlyList<string> missing = interviewState.Missing();

        if (missing.Count > 0)
            return $"Not completed: {string.Join(", ", missing)} still unset. "
                   + (missing.Count == 1 ? "Set it and call this again." : "Set them and call this again.");

        interviewState.ReadyForReview = true;

        return "This pass is settled. Say it is done and what comes next: "
               + interviewState.Pass switch
               {
                   InterviewStep.DomainMapper => $"the first of the {interviewState.Map.Count} kinds of request you mapped, taken one at a time until every one has its agent.",
                   InterviewStep.AgentTarget => "how this agent should sound to the people who write in.",
                   InterviewStep.AgentPersonality => "the toolkit — what this agent has to reach for outside the conversation.",
                   InterviewStep.AgentToolkit => "the workflows — what this agent has to do in a fixed order, which may be nothing.",
                   InterviewStep.AgentWorkflows => "what this agent is the one to be asked about, which is what another agent of theirs reads before it asks.",
                   InterviewStep.AgentTerritory => "the agent's own instructions and the way it presents what its tools return.",
                   InterviewStep.DomainColleagues => "nothing — the domain is finished and they land on it whole, to read, weigh and take away.",
                   _ => "the agent joins the domain and they can review or export it."
               };
    }

    /// <summary>
    /// Finds a declared tool by exact name.
    /// </summary>
    /// <remarks>
    /// Ordinal, because the name becomes a C# method name and the model calls the tool by it exactly.
    /// Two tools differing only in case are two tools here and one collision at startup, which is a
    /// finding rather than something to paper over by matching loosely.
    /// </remarks>
    private ToolDraft? Find(string? toolName) =>
        interviewState.Agent.Tools.FirstOrDefault(t =>
            string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));

    /// <summary>
    /// Says what is wrong with a name that has to survive into C#, or nothing if it is fine.
    /// </summary>
    /// <remarks>
    /// Shape only and deliberately not the compiler: the point is to catch what a domain
    /// conversation actually produces — a space, a hyphen, a leading digit — while it is still free
    /// to change. The casing check is separate because it is a convention rather than a rule and it
    /// is worth stating: the generated method and the declaration have to read like the framework's
    /// own.
    /// </remarks>
    /// <remarks>
    /// Internal rather than private: <see cref="CoherenceApplyTools"/> writes into agents that are
    /// no longer the one interview state under construction, but the identifier it writes still has
    /// to survive into the same C#, by the same rule.
    /// </remarks>
    internal static string IdentifierComplaint(string name, string what, bool pascalCase)
    {
        bool shapeOk = name.Length > 0
                       && (char.IsAsciiLetter(name[0]) || name[0] == '_')
                       && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

        if (!shapeOk)
            return $"But '{name}' cannot be a C# identifier and the {what} becomes C# verbatim: "
                   + "it must start with a letter and carry only letters, digits and underscores. Call again with one that can.";

        bool casingOk = pascalCase ? char.IsAsciiLetterUpper(name[0]) : char.IsAsciiLetterLower(name[0]);

        return casingOk
            ? string.Empty
            : $"But the {what} should be {(pascalCase ? "PascalCase" : "camelCase")}, the way the framework's own are. Call again to fix it.";
    }



    /// <summary>
    /// Reports whether a section landed inside the size its doctrine gives it.
    /// </summary>
    /// <remarks>
    /// Recorded either way. The size is a shape, not a gate: a Target of five sentences is still
    /// better than no Target and Alembic is told so it can tighten rather than blocked so it must.
    /// </remarks>
    private static string Shaped(string section, string? value, int minimum, int maximum)
    {
        int sentences = CountSentences(value);

        if (sentences >= minimum && sentences <= maximum)
            return $"{section} recorded.";

        return $"{section} recorded, but it runs to {sentences} "
               + (sentences == 1 ? "sentence" : "sentences")
               + $" where this section's shape is {minimum} to {maximum}. "
               + (sentences < minimum
                   ? "It is saying less than the section is for. Fill it out and call again."
                   : "Tighten it and call again.");
    }

    /// <summary>
    /// Counts sentences crudely — terminal punctuation followed by a space or the end.
    /// </summary>
    private static int CountSentences(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        string trimmed = value.Trim();
        int count = 0;

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] is not ('.' or '!' or '?'))
                continue;

            if (i == trimmed.Length - 1 || char.IsWhiteSpace(trimmed[i + 1]))
                count++;
        }

        // Prose that never reaches terminal punctuation is still one sentence, not none.
        return count == 0 ? 1 : count;
    }
}
