using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Models;
using Alembic.Services;
using Morgana.Contracts;
using Morgana.AI;
using Morgana.AI.Attributes;
using Morgana.AI.Services;

namespace Alembic.Tools;

/// <summary>
/// The tools Alembic calls while conducting a pass of the interview, each answering the model with a <see cref="ToolReply"/>.
/// </summary>
/// <remarks>Which of these exist in a pass is decided by <c>alembic.json</c>: a pass holding no tool for a section cannot write it.</remarks>
public class InterviewTools
{
    /// <summary>The intent name the classifier's fallback owns: no authored agent may take it.</summary>
    private const string ReservedFallbackIntent = Constants.Intents.Other;

    /// <summary>The scope of a parameter Morgana resolves from the session's own context variables.</summary>
    private const string ContextScope = Constants.Scopes.Context;

    /// <summary>The scope of a parameter the agent obtains from the user in conversation.</summary>
    private const string RequestScope = Constants.Scopes.Request;

    /// <summary>The interview whose map and agent in hand these tools write into.</summary>
    private readonly InterviewState interviewState;

    /// <summary>Whether this pass has already read Morgana's own layer of the composed prompt.</summary>
    private bool framework;

    /// <summary>The domain being built or evolved, read for the intents and agents already committed.</summary>
    private readonly IDraftStateService draftStateService;

    /// <summary>The deterministic checks whose findings about the agent in hand are reported to the model.</summary>
    private readonly IDraftValidationService draftValidationService;

    /// <summary>Composes the prompt that the authored agent's model will read.</summary>
    private readonly IRecapService recapService;

    /// <summary>The authority on <c>alembic.json</c>, which holds every sentence these tools answer with.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>
    /// Builds the toolset over one interview and the services its replies draw on.
    /// </summary>
    public InterviewTools(
        InterviewState interviewState,
        IDraftStateService draftStateService,
        IDraftValidationService draftValidationService,
        IRecapService recapService,
        IAlembicPromptService alembicPromptService)
    {
        // One instance serves one pass of one interview, so the services are held as given.
        this.interviewState = interviewState;
        this.draftStateService = draftStateService;
        this.draftValidationService = draftValidationService;
        this.recapService = recapService;
        this.alembicPromptService = alembicPromptService;
    }

    /// <summary>
    /// Fetches a sentence that a tool answers the model with, from the <c>ToolInjections</c> of the <c>Alembic</c> prompt.
    /// </summary>
    private string Said(string injectionName, params (string Placeholder, string Value)[] values) =>
        alembicPromptService.ResolveToolInjection(injectionName, values);

    /// <summary>
    /// Puts one kind of request on the domain map, or revises one already there.
    /// </summary>
    /// <remarks>Revising keeps the entry's place, since the interview walks the map in the order the client thought of their business.</remarks>
    /// <param name="name">Bare lowercase word — becomes a C# attribute argument and a prompt ID.</param>
    /// <param name="description">What routes here, weighed by the classifier against every other intent's.</param>
    /// <param name="label">The quick-reply button text a user reads, if this call is also settling it.</param>
    /// <param name="defaultValue">The sentence pressing that button sends, as if the user had typed it.</param>
    [Description("Puts one process the client is handing to Morgana on the domain map, or revises one already there. It writes the whole intent — what it is called, what routes to it and the button a user presses to start it — because all three are read against the other entries and not on their own. One entry becomes one intent and one agent. Call it as soon as an entry is clear and again to add the button or sharpen the description.")]
    [RequiresApproval(false)]
    public ToolReply DeclareIntent(
        [Description("A single bare lowercase word, no spaces or punctuation (e.g. 'billing', 'appointments'). It becomes a C# attribute argument and a prompt ID. 'other' is reserved for the classifier's fallback and will be refused.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("The kinds of request that belong here — 'requests to check an order's status, place a new order, or cancel one' — never what an agent does about them. The classifier reads this and nothing else, weighed against every other entry's description.")] [ToolParameter(Records.ToolScope.Request)] string description,
        [Description("A leading emoji then one to three words (e.g. '📄 Billing'). It is read on a button beside every other intent's button, so it names what is got, not what the system does. Omit it on a first pass and add it when the set is written together.")] [ToolParameter(Records.ToolScope.Request)] string? label = null,
        [Description("The sentence that button sends, which opens this agent's presentation of itself and its menu: a first-person question about what it can do, never a request it would act on (e.g. 'What can you do about my dog's appointments?'). Omit it on a first pass and add it with the label.")] [ToolParameter(Records.ToolScope.Request)] string? defaultValue = null)
    {
        // The name becomes an attribute argument and a prompt ID, so a blank one cannot be recorded.
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("IntentNameMissing"));

        // The fallback intent belongs to the classifier and never to an authored agent.
        if (string.Equals(cleanName, ReservedFallbackIntent, StringComparison.OrdinalIgnoreCase))
            return ToolReply.Refused(Said("IntentReserved", ("name", ReservedFallbackIntent)));

        // An intent already committed to the domain cannot be declared again by this interview.
        if (draftStateService.Current?.Intents.Any(i =>
                string.Equals(i.Name, cleanName, StringComparison.OrdinalIgnoreCase)) == true)
            return ToolReply.Refused(Said("IntentTaken", ("name", cleanName)));

        // An entry of the same name on the map is revised in place, otherwise a new authored entry is drawn.
        IntentDraft? match = interviewState.Map.FirstOrDefault(i =>
            string.Equals(i.Name, cleanName, StringComparison.OrdinalIgnoreCase));

        // The reply to the model says whether the entry was revised or newly drawn.
        bool revision = match is not null;
        IntentDraft intent = match ?? new IntentDraft { Origin = Provenance.Authored };

        // The name and the description are what the classifier routes on.
        intent.Name = cleanName;
        intent.Description = description?.Trim();

        // Left alone when the call omits them, so declaring an entry early and giving it a button
        // later is one entry revised twice rather than a button silently thrown away.
        if (!string.IsNullOrWhiteSpace(label))
            intent.Label = label.Trim();

        // The default value is left alone for the same reason.
        if (!string.IsNullOrWhiteSpace(defaultValue))
            intent.DefaultValue = defaultValue.Trim();

        // Only a new entry takes a place at the end of the map.
        if (!revision)
            interviewState.Map.Add(intent);

        // The entry is recorded whatever its shape: the model is told what to put right in the same turn.
        List<string> complaints = [];

        // A name off the bare lowercase convention cannot serve as an attribute argument.
        if (!cleanName.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c)))
            complaints.Add(Said("IntentNameNotBare"));

        // Without a description the classifier has nothing to route on.
        if (string.IsNullOrWhiteSpace(description))
            complaints.Add(Said("IntentDescriptionMissing"));

        // The button is whole only with both its label and the sentence it sends.
        if (string.IsNullOrWhiteSpace(intent.Label) || string.IsNullOrWhiteSpace(intent.DefaultValue))
            complaints.Add(Said("IntentButtonMissing"));

        // The reply says whether the entry was added at its position or revised and appends the complaints.
        return new ToolReply((revision
                   ? Said("IntentRevised", ("name", cleanName))
                   : Said("IntentAdded", ("name", cleanName), ("position", interviewState.Map.Count.ToString(CultureInfo.InvariantCulture)))
               )
               + (complaints.Count > 0 ? " " + string.Join(" ", complaints) : string.Empty));
    }

    /// <summary>
    /// Takes a kind of request off the map.
    /// </summary>
    [Description("Takes an entry off the map, for when two turn out to be one or the client says it is not their business.")]
    [RequiresApproval(false)]
    public ToolReply DropIntent(
        [Description("The name of the entry to remove.")] [ToolParameter(Records.ToolScope.Request)] string name)
    {
        // The entry is dropped by name and the count says whether it was on the map.
        int removals = interviewState.Map.RemoveAll(i =>
            string.Equals(i.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        // A removal is confirmed while a name that was never on the map is refused.
        return removals > 0
            ? new ToolReply(Said("IntentDropped", ("name", name ?? string.Empty)))
            : ToolReply.Refused(Said("IntentNotOnMap", ("name", name ?? string.Empty)));
    }

    /// <summary>
    /// Returns the domain map as it currently stands.
    /// </summary>
    /// <remarks>Two descriptions overlap only when read side by side: the model weighs the map whole before settling.</remarks>
    [Description("Returns the map as it stands, in order. Read it before settling: whether two descriptions could claim the same message is only visible with both in front of you.")]
    [RequiresApproval(false)]
    public ToolReply GetDomainMap()
    {
        // An empty map is reported so the model draws it before settling anything.
        if (interviewState.Map.Count == 0)
            return new ToolReply(Said("MapEmpty"));

        // Each entry is listed in the client's order with its routing description and its button.
        return new ToolReply(Said("MapHeader") + "\n"
               + string.Join("\n", interviewState.Map.Select((i, n) =>
                   $"{n + 1}. {i.Name}: {i.Description ?? "(nothing said about what routes here)"}"
                   + $"\n    button: {i.Label ?? "(none)"} → \"{i.DefaultValue ?? "(nothing)"}\"")));
    }

    /// <summary>
    /// Records the agent's Target section.
    /// </summary>
    /// <remarks>The last call stands. The reply tells the model whether the prose fits the section's shape without blocking it.</remarks>
    [Description("Records the agent's TARGET section: what it does well and existentially, then what it is significant to say it does not do. Only edges that will actually be walked into — a limit nobody would test is noise. This section opens the composed prompt, so a capability claimed here and not backed by a tool is the most expensive mistake available to you.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentTarget(
        [Description("Two to four sentences, addressed to the agent as 'you'. Purpose first, then edges. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string target)
    {
        // The section is stored as written: its label is added when the prompt is composed.
        interviewState.Agent.Target = target?.Trim();

        // The reply reports the sentence count against the Target's two to four.
        return new ToolReply(Shaped("Target", target, 2, 4));
    }

    /// <summary>
    /// Records what a colleague reads before consulting this agent.
    /// </summary>
    /// <remarks>It names a territory and never lists what the agent can do, since a caller handed an inventory rules its question out.</remarks>
    [Description("Records what a colleague reads before consulting this agent: the scope of its agent, addressed to another agent instead of to itself. A territory, in one or two sentences — what falls here — never a list of what the agent can do, which a caller reads as grounds to rule its question out. Never asked of the client and never shown to them: you write it from the TARGET you have just settled.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentTerritory(
        [Description("The statement, without its section marker.")] [ToolParameter(Records.ToolScope.Request)] string territory)
    {
        // The territory is what a colleague reads on the agent's card before consulting it.
        interviewState.Agent.Territory = territory?.Trim();

        // The reply reports the sentence count against the territory's one to three.
        return new ToolReply(Shaped("Territory", territory, 1, 3));
    }

    /// <summary>
    /// Records the agent's Personality section.
    /// </summary>
    /// <remarks>Only the <c>AgentPersonality</c> pass holds this tool: it is the one place a voice is written.</remarks>
    [Description("Records the agent's PERSONALITY section: the empathy, language, tone and humanity it meets a conversation with. Voice only — a sentence telling the agent what to do belongs in its instructions. Name which facet of Morgana this agent is; do not list adjectives.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentPersonality(
        [Description("Two to three sentences, addressed to the agent as 'you'. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string personality)
    {
        // The voice is stored as written, without its section label.
        interviewState.Agent.Personality = personality?.Trim();

        // The reply reports the sentence count against the voice's two to three.
        return new ToolReply(Shaped("Personality", personality, 2, 3));
    }

    /// <summary>
    /// Records the agent's Instructions section.
    /// </summary>
    /// <remarks>Instructions speak about the agent's tools, so no pass before the toolkit exists holds this tool.</remarks>
    [Description("Records the agent's INSTRUCTIONS section: how it does what it does, what it is trying to achieve on the way and what it is significant to say it must not do. The order of the work and its refusals. Only what is true of this domain.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentInstructions(
        [Description("Four to twelve sentences, addressed to the agent as 'you', in the order the work happens. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string instructions)
    {
        // The instructions are stored as written, without their section label.
        interviewState.Agent.Instructions = instructions?.Trim();

        // The reply reports the sentence count against the Instructions' two to five.
        return new ToolReply(Shaped("Instructions", instructions, 2, 5));
    }

    /// <summary>
    /// Records the agent's Formatting section.
    /// </summary>
    /// <remarks>The upper bound is wide because this section also names the exact card shape or button payload of each tool.</remarks>
    [Description("Records the agent's FORMATTING section: how it presents what its own tools return. The one thing no global policy can know for it — Morgana cannot know that an invoice leads with its due date, or that this agent's own confirm-or-cancel deserves two buttons. Say nothing about markdown or length, those are hers; if a button or a card belongs here, name it exactly.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentFormatting(
        [Description("Addressed to the agent as 'you', about this agent's own data: normally two to five sentences, longer only where naming an exact card shape per tool or an exact button payload takes the room it takes. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string formatting)
    {
        // The formatting is stored as written, without its section label.
        interviewState.Agent.Formatting = formatting?.Trim();

        // The reply reports the sentence count against the Formatting's two to ten.
        return new ToolReply(Shaped("Formatting", formatting, 2, 10));
    }

    /// <summary>
    /// Opens a tool, or revises the description and the approval requirement of one already open.
    /// </summary>
    /// <remarks>Revising keeps the parameters: a tool's contract is settled over several turns and re-declaring it must not empty it.</remarks>
    [Description("Opens a tool, or revises one already open — revising keeps its parameters. Declare it as soon as the action is described, before you know its inputs.")]
    [RequiresApproval(false)]
    public ToolReply DeclareTool(
        [Description("PascalCase, no spaces or punctuation, a verb the agent performs (e.g. 'GetInvoices', 'CancelBooking'). It becomes a C# method name verbatim.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("What the tool does and when to call it, never the fields it returns: those are its record's own contract. Two to four sentences. Say what it does not cover where a neighbouring tool covers it. A description that names only the action leaves the timing to guesswork.")] [ToolParameter(Records.ToolScope.Request)] string description,
        [Description("True when the tool changes something real — it places, books, bills, sends, cancels or deletes — so Morgana runs it only once the user has approved that very call. False when it only looks something up. Inferred from what the tool does, never asked.")] [ToolParameter(Records.ToolScope.Request)] bool requiresApproval)
    {
        // The name becomes a C# method name, so a blank one cannot be recorded.
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("ToolNameMissing"));

        // A tool of the same name on the agent is revised in place, otherwise a new authored tool is opened.
        ToolDraft? match = Find(cleanName);
        bool revision = match is not null;

        ToolDraft tool = match ?? new ToolDraft { Name = cleanName, Origin = Provenance.Authored };

        // The description is what the model reads to decide when to call; the approval says whether the user must confirm the call.
        tool.Description = description?.Trim();
        tool.RequiresExecutionApproval = requiresApproval;

        // Only a new tool joins the agent's toolkit.
        if (!revision)
            interviewState.Agent.Tools.Add(tool);

        // Reported, never rewritten: the name is domain vocabulary and a silent correction leaves
        // Alembic telling the client one word while the configuration carries another.
        string complaint = IdentifierComplaint(alembicPromptService, cleanName, "tool name", pascalCase: true);

        // The reply says whether the tool was opened or revised and appends the naming and description complaints.
        return new ToolReply((revision ? Said("ToolRevised", ("name", cleanName)) : Said("ToolDeclared", ("name", cleanName)))
               + (complaint.Length > 0 ? " " + complaint : string.Empty)
               + (string.IsNullOrWhiteSpace(description) ? " " + Said("ToolDescriptionMissing") : string.Empty));
    }

    /// <summary>
    /// Adds a parameter to a tool, or revises one already there.
    /// </summary>
    /// <remarks>Revision keeps the position, since the declaration order becomes the C# parameter order.</remarks>
    /// <param name="toolName">The already-declared tool this parameter belongs to.</param>
    /// <param name="name">camelCase — becomes the C# method's parameter name, matched by name not position.</param>
    /// <param name="description">What the model reads when deciding what to pass here.</param>
    /// <param name="scope">
    /// <c>"context"</c> if Morgana resolves it from the session, <c>"request"</c> if the agent must
    /// obtain it in conversation.
    /// </param>
    /// <param name="required">Whether the call fails without it — required parameters must precede optional ones and only a request parameter may be optional.</param>
    /// <param name="shared">Whether a resolved context value is published for other agents to hydrate from; only a <c>"context"</c> parameter may be shared.</param>
    [Description("Adds a parameter to a declared tool, or revises one already there by name and in place. Declaration order becomes the C# parameter order, which cannot put a required parameter after an optional one.")]
    [RequiresApproval(false)]
    public ToolReply SetToolParameter(
        [Description("The exact name of a tool you have already declared.")] [ToolParameter(Records.ToolScope.Request)] string toolName,
        [Description("camelCase, no spaces or punctuation (e.g. 'invoiceId'). It becomes the C# parameter name verbatim.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("What the value is and what a good one looks like, with an example where one helps. This reaches the agent's model through the tool's JSON schema and nowhere else. Never say here how a context value is found or when it is asked for: Morgana settles that herself.")] [ToolParameter(Records.ToolScope.Request)] string description,
        [Description("'request' if the value is stated in the conversation, 'context' if the agent should already hold it from earlier. Infer it from the setup; do not ask parameter by parameter.")] [ToolParameter(Records.ToolScope.Request)] string scope,
        [Description("Whether the agent must supply this value on every call. Optional parameters come after the required ones and only a 'request' parameter may be optional.")] [ToolParameter(Records.ToolScope.Request)] bool required,
        [Description("Whether the resolved value is published to the whole conversation, so other agents use it without asking again. Only a 'context' parameter may be shared. True for an identity the domain establishes once, false for one agent's own working value.")] [ToolParameter(Records.ToolScope.Request)] bool shared)
    {
        // The parameter belongs to a tool declared earlier in the pass.
        if (Find(toolName) is not { } tool)
            return ToolReply.Refused(Said("ParameterToolMissing", ("tool", toolName ?? string.Empty)));

        // The name becomes a C# parameter name, so a blank one cannot be recorded.
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("ParameterNameMissing"));

        // The scope is kept as said when it is neither known value, so the complaint and validation both name it.
        string? resolvedScope = ResolveScope(scope);

        // A parameter of the same name on this tool is revised in place.
        ToolParameterDraft? match = tool.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, cleanName, StringComparison.Ordinal));

        // The reply to the model says whether the parameter was revised or newly opened.
        bool revision = match is not null;
        ToolParameterDraft parameter = match ?? new ToolParameterDraft { Name = cleanName };

        // The parameter takes the description, scope and flags the model gave it.
        parameter.Description = description?.Trim();
        parameter.Scope = resolvedScope;
        parameter.Required = required;
        parameter.Shared = shared;

        // Only a new parameter joins the end of the tool's list.
        if (!revision)
            tool.Parameters.Add(parameter);

        // The parameter is recorded whatever its shape: the model is told what to put right in the same turn.
        List<string> complaints = [];

        // A name that cannot survive into C# is reported.
        string identifier = IdentifierComplaint(alembicPromptService, cleanName, "parameter name", pascalCase: false);
        if (identifier.Length > 0)
            complaints.Add(identifier);

        // Scope, sharing and optionality combinations that Morgana refuses at startup are reported.
        complaints.AddRange(ScopeComplaints(alembicPromptService, resolvedScope, required, shared));

        // The order is the signature, so an optional parameter followed by a required one is not a
        // preference: C# could not declare the signature.
        if (Enumerable.Range(0, tool.Parameters.Count).Any(index => DraftValidationService.RequiredFollowsOptional(tool.Parameters, index)))
            complaints.Add(Said("ParameterRequiredAfterOptional"));

        // The reply confirms the parameter on its tool and appends the complaints.
        return new ToolReply(Said("ParameterRecorded", ("name", cleanName), ("tool", tool.Name ?? string.Empty))
               + (complaints.Count > 0 ? " " + string.Join(" ", complaints) : string.Empty));
    }

    /// <summary>
    /// Reads a scope the model wrote: one of the two scopes, the text as said where it is neither or <c>null</c> where it is empty.
    /// </summary>
    /// <remarks>Internal because <see cref="CoherenceApplyTools"/> writes parameters under the same rules.</remarks>
    internal static string? ResolveScope(string? scope)
    {
        // Case and padding are dropped so that "Context " reads as the scope it means.
        string cleanScope = (scope ?? string.Empty).Trim().ToLowerInvariant();

        // A scope that is neither known value is kept as said, so that the complaint and validation both name it.
        return cleanScope switch
        {
            ContextScope => ContextScope,
            RequestScope => RequestScope,
            "" => null,
            _ => cleanScope
        };
    }

    /// <summary>
    /// Lists what Morgana refuses at startup in a parameter's scope, sharing and optionality.
    /// </summary>
    /// <remarks>Internal because <see cref="CoherenceApplyTools"/> writes parameters under the same rules.</remarks>
    /// <param name="scope">The scope as <see cref="ResolveScope"/> read it.</param>
    /// <param name="required">Whether the call fails without the parameter.</param>
    /// <param name="shared">Whether the resolved value is published for other agents.</param>
    internal static List<string> ScopeComplaints(IAlembicPromptService alembicPromptService, string? scope, bool required, bool shared)
    {
        List<string> complaints = [];

        // A scope other than context or request fails the framework's startup check.
        if (!DraftValidationService.IsScope(scope))
            complaints.Add(alembicPromptService.ResolveToolInjection("ScopeUnknown",
                ("scope", scope ?? string.Empty), ("context", ContextScope), ("request", RequestScope)));

        // A value asked of the user is never published to the conversation.
        if (DraftValidationService.IsSharedRequest(scope, shared))
            complaints.Add(alembicPromptService.ResolveToolInjection("ScopeRequestShared"));

        // A value the session already holds cannot be optional.
        if (DraftValidationService.IsOptionalContext(scope, required))
            complaints.Add(alembicPromptService.ResolveToolInjection("ScopeContextOptional"));

        // The complaints are handed to the caller, which appends them to its reply.
        return complaints;
    }

    /// <summary>
    /// Removes a parameter from a tool.
    /// </summary>
    [Description("Removes a parameter from a tool. Also how a parameter moves: drop it and add it again in the position you want.")]
    [RequiresApproval(false)]
    public ToolReply DropToolParameter(
        [Description("The exact name of the tool.")] [ToolParameter(Records.ToolScope.Request)] string toolName,
        [Description("The exact name of the parameter to remove.")] [ToolParameter(Records.ToolScope.Request)] string parameterName)
    {
        // A parameter can only be dropped from a tool the pass has declared.
        if (Find(toolName) is not { } tool)
            return ToolReply.Refused(Said("DropToolUnknown", ("tool", toolName ?? string.Empty)));

        // The parameter is dropped by exact name and the count says whether it was there.
        int removals = tool.Parameters.RemoveAll(p =>
            string.Equals(p.Name, parameterName?.Trim(), StringComparison.Ordinal));

        // A removal is confirmed while a name that was never there is refused.
        return removals > 0
            ? new ToolReply(Said("ParameterDropped", ("name", parameterName ?? string.Empty), ("tool", tool.Name ?? string.Empty)))
            : ToolReply.Refused(Said("ParameterAbsent", ("tool", tool.Name ?? string.Empty), ("name", parameterName ?? string.Empty)));
    }

    /// <summary>
    /// Adds a field to what a tool hands back, or revises one already there.
    /// </summary>
    /// <remarks>The field becomes a property of the record the tool's method returns, so its type is written exactly as C# writes it.</remarks>
    /// <param name="toolName">The already-declared tool whose result this field belongs to.</param>
    /// <param name="name">camelCase as the model reads it — the record's property is this name with its first letter upper-cased.</param>
    /// <param name="description">What the model learns from this field of the result.</param>
    /// <param name="type">The C# type of the record's property, such as <c>string</c>, <c>decimal</c> or <c>List&lt;string&gt;</c>; <c>string</c> for the failure.</param>
    [Description("Adds a field to what a declared tool hands back, or revises one already there by name. The agent's model reads exactly these fields in the tool's result and the client's code fills exactly these. The field named 'error' is the failure: it holds why the call failed and stays empty whenever the call succeeds.")]
    [RequiresApproval(false)]
    public ToolReply SetToolReturn(
        [Description("The exact name of a tool you have already declared.")] [ToolParameter(Records.ToolScope.Request)] string toolName,
        [Description("camelCase, no spaces or punctuation (e.g. 'orderId'). It is the name the agent's model reads in the result.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("What the field holds, with an example where one helps.")] [ToolParameter(Records.ToolScope.Request)] string description,
        [Description("The C# type of the field exactly as the record writes it, preferring a simple one: string, int, long, decimal, bool, DateOnly or List<string>. The field named 'error' is always string.")] [ToolParameter(Records.ToolScope.Request)] string type)
    {
        // A result field belongs to a tool declared earlier in the pass.
        if (Find(toolName) is not { } tool)
            return ToolReply.Refused(Said("ReturnToolMissing", ("tool", toolName ?? string.Empty)));

        // The name becomes a record property, so a blank one cannot be recorded.
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("ReturnNameMissing"));

        // The field is built from what the model said; a blank type stays unset and is reported below.
        string cleanType = (type ?? string.Empty).Trim();
        ToolReturnDraft field = new()
        {
            Name = cleanName,
            Description = (description ?? string.Empty).Trim(),
            Type = cleanType.Length == 0 ? null : cleanType
        };

        // A field of the same name on this tool is replaced in place.
        int index = tool.Returns.FindIndex(r => string.Equals(r.Name, cleanName, StringComparison.Ordinal));
        if (index >= 0)
            tool.Returns[index] = field;
        // A new field joins the end of the result.
        else
            tool.Returns.Add(field);

        // The field is recorded whatever its shape: the model is told what to put right in the same turn.
        List<string> complaints = [];

        // A name that cannot survive into C# is reported.
        string identifier = IdentifierComplaint(alembicPromptService, cleanName, "field name", pascalCase: false);
        if (identifier.Length > 0)
            complaints.Add(identifier);

        // Startup reads the record back through camelCase, so a name that changes on the way out and
        // back would be declared here and never found there.
        if (!DraftValidationService.KeepsNameInCSharp(cleanName))
        {
            string property = CodeEmitService.PropertyName(cleanName);
            string converted = JsonNamingPolicy.CamelCase.ConvertName(property);

            complaints.Add(Said("ReturnNameLost", ("name", cleanName), ("property", property), ("converted", converted)));
        }

        // A field without a type cannot be emitted as a property.
        if (cleanType.Length == 0)
            complaints.Add(Said("ReturnTypeMissing"));
        // A type that is not valid C# fails the generated code's build.
        else if (!DraftValidationService.ParsesAsType(cleanType))
            complaints.Add(Said("ReturnTypeInvalid", ("type", cleanType)));

        // The failure field carries the reason of the failure, so it can only be text.
        if (DraftValidationService.IsNonTextFailureField(cleanName, cleanType))
            complaints.Add(Said("ReturnFailureNotText", ("name", cleanName), ("tool", tool.Name ?? string.Empty)));

        // The reply confirms the field on its tool and appends the complaints.
        return new ToolReply(Said("ReturnRecorded", ("name", cleanName), ("tool", tool.Name ?? string.Empty))
               + (complaints.Count > 0 ? " " + string.Join(" ", complaints) : string.Empty));
    }

    /// <summary>
    /// Removes a field from what a tool hands back.
    /// </summary>
    [Description("Removes a field from what a tool hands back.")]
    [RequiresApproval(false)]
    public ToolReply DropToolReturn(
        [Description("The exact name of the tool.")] [ToolParameter(Records.ToolScope.Request)] string toolName,
        [Description("The exact name of the field to remove.")] [ToolParameter(Records.ToolScope.Request)] string name)
    {
        // A result field can only be dropped from a tool the pass has declared.
        if (Find(toolName) is not { } tool)
            return ToolReply.Refused(Said("DropToolUnknown", ("tool", toolName ?? string.Empty)));

        // The field is dropped by exact name and the count says whether it was there.
        int removals = tool.Returns.RemoveAll(r =>
            string.Equals(r.Name, name?.Trim(), StringComparison.Ordinal));

        // A removal is confirmed while a name that was never there is refused.
        return removals > 0
            ? new ToolReply(Said("ReturnDropped", ("name", name ?? string.Empty), ("tool", tool.Name ?? string.Empty)))
            : ToolReply.Refused(Said("ReturnAbsent", ("tool", tool.Name ?? string.Empty), ("name", name ?? string.Empty)));
    }

    /// <summary>
    /// Removes a tool and everything on it.
    /// </summary>
    /// <remarks>Workflow steps that still name the tool are reported and left for the workflows pass to repair.</remarks>
    [Description("Removes a tool and every parameter on it. Use it when the agent turns out to have no such job, or when you have split one action into two tools that cannot be told apart.")]
    [RequiresApproval(false)]
    public ToolReply DropTool(
        [Description("The exact name of the tool to remove.")] [ToolParameter(Records.ToolScope.Request)] string toolName)
    {
        // The tool is dropped by exact name and the count says whether it was in the toolkit.
        string cleanName = toolName?.Trim() ?? string.Empty;

        int removals = interviewState.Agent.Tools.RemoveAll(t =>
            string.Equals(t.Name, cleanName, StringComparison.Ordinal));

        // A name that was never in the toolkit is refused.
        if (removals == 0)
            return ToolReply.Refused(Said("DropToolUnknown", ("tool", toolName ?? string.Empty)));

        // Each workflow step still offering the dropped tool is reported to the model.
        List<string> orphans =
        [.. interviewState.Agent.Workflows.SelectMany(workflow => workflow.Steps
                .Where(step => step.Tools.Contains(cleanName, StringComparer.Ordinal))
                .Select(step => Said("ToolStillInStep",
                    ("tool", cleanName), ("step", step.Name), ("workflow", workflow.Name ?? string.Empty))))];

        // The reply confirms the removal and lists the steps left offering the tool.
        return new ToolReply(Said("ToolDropped", ("name", toolName ?? string.Empty))
               + (orphans.Count > 0 ? " " + string.Join(" ", orphans) : string.Empty));
    }

    /// <summary>
    /// Returns the toolkit as it currently stands.
    /// </summary>
    [Description("Returns the agent's tools, their parameters and what each hands back.")]
    [RequiresApproval(false)]
    public ToolReply GetToolkit()
    {
        // An agent without tools is told so, with its workflows still shown beside the empty toolkit.
        if (interviewState.Agent.Tools.Count == 0)
            return new ToolReply(Said("ToolkitEmpty") + "\n\n"
                   + DescribeWorkflows(alembicPromptService, interviewState.Agent.Workflows));

        // Each tool is listed with its approval requirement then its parameters with scope and flags then the fields it hands back.
        IEnumerable<string> lines = interviewState.Agent.Tools.Select(t =>
            $"- {t.Name}{(t.RequiresExecutionApproval ? " " + Said("ToolWaitsForApproval") : string.Empty)}{DescribeOffering(alembicPromptService, t.Offering)}: {t.Description ?? "(no description)"}"
            + (t.Parameters.Count == 0
                ? "\n    " + Said("ToolTakesNothing")
                : string.Concat(t.Parameters.Select(p =>
                    $"\n    {p.Name} [{p.Scope ?? Said("NoScopeYet")}"
                    + (p.Required ? "" : ", " + Said("ParameterOptional"))
                    + (p.Shared ? ", " + Said("ParameterShared") : "")
                    + $"]: {p.Description ?? "(no description)"}")))
            + (t.Returns.Count == 0
                ? "\n    " + Said("ToolHandsBackNothing")
                : string.Concat(t.Returns.Select(r =>
                    $"\n    returns {r.Name} ({r.Type ?? Said("NoTypeYet")}){(r.Name == Constants.Workflows.FailureField ? " [" + Said("ReturnIsFailure") + "]" : string.Empty)}: "
                    + (string.IsNullOrWhiteSpace(r.Description) ? "(no description)" : r.Description)))));

        // The toolkit and the workflows built on it are read back together.
        return new ToolReply(Said("ToolkitHeader") + "\n" + string.Join("\n", lines)
               + "\n\n" + DescribeWorkflows(alembicPromptService, interviewState.Agent.Workflows));
    }

    /// <summary>
    /// Writes the agent's whole menu at once, replacing the one before.
    /// </summary>
    /// <remarks>The ordinal is the position in the array and never the model's to write, so the numbering cannot clash.</remarks>
    /// <param name="entries">The buttons in menu order, each naming the tool or the workflow it presses and carrying its label.</param>
    [Description("Writes this agent's menu whole, replacing the one before: the buttons a user finds on arriving at this agent from Morgana's own, each pressing one tool or one workflow of this agent. A label is both what the button shows and the message its press sends, so it reads as the user's own request: a leading emoji then two to five words ('🧾 My recent invoices'). Offer what a customer would come here to start. Never put on it a tool that only serves another or one that waits for approval; the first tool of a workflow never goes on it either, the workflow does. An empty array leaves the agent with no menu.")]
    [RequiresApproval(false)]
    public ToolReply SetMenu(
        [Description("The buttons of the menu in order, first button first. Each entry is the exact name of a tool or a workflow of this agent.")] [ToolParameter(Records.ToolScope.Request)] List<MenuEntry> entries)
    {
        // The menu as the model wrote it: an entry name and a label, in button order.
        // A missing text reads as blank: a blank entry is told as unknown and a blank label is refused by the framework's own check.
        List<(string Entry, string Label)> requestedEntries =
            [.. entries.Select(menuEntry => ((menuEntry.Entry ?? string.Empty).Trim(), (menuEntry.Label ?? string.Empty).Trim()))];

        // An empty menu withdraws every offering the agent carries.
        if (requestedEntries.Count == 0)
        {
            ApplyOfferings(new Dictionary<string, Records.ToolOffering>(StringComparer.Ordinal));
            return new ToolReply(Said("MenuCleared"));
        }

        // The candidate offerings by entry name, built before anything is recorded so a refusal leaves the previous menu untouched.
        Dictionary<string, Records.ToolOffering> candidateOfferings = new(StringComparer.Ordinal);

        foreach ((string entryName, string label) in requestedEntries)
        {
            // Tools and workflows are told apart by exact name, which is how the framework looks them up.
            bool namesTool = interviewState.Agent.Tools.Any(tool => string.Equals(tool.Name, entryName, StringComparison.Ordinal));
            bool namesWorkflow = interviewState.Agent.Workflows.Any(workflow => string.Equals(workflow.Name, entryName, StringComparison.Ordinal));

            // An entry naming nothing of this agent has no button to press.
            if (!namesTool && !namesWorkflow)
                return ToolReply.Refused(Said("MenuEntryUnknown", ("entry", entryName)));

            // A name held by both would press an unpredictable one of them.
            if (namesTool && namesWorkflow)
                return ToolReply.Refused(Said("MenuEntryAmbiguous", ("entry", entryName)));

            // One entry twice would be two buttons for the same press.
            if (candidateOfferings.ContainsKey(entryName))
                return ToolReply.Refused(Said("MenuEntryTwice", ("entry", entryName)));

            // The ordinal is the position in the menu, counting from 1.
            candidateOfferings[entryName] = new Records.ToolOffering(candidateOfferings.Count + 1, label);
        }

        // The agent as the framework would read it with the candidate menu in place.
        List<Records.ToolDefinition> candidateTools =
        [
            .. interviewState.Agent.Tools.Select(tool => DraftProjection.ToToolDefinition(tool) with
            {
                Offering = tool.Name is not null && candidateOfferings.TryGetValue(tool.Name, out Records.ToolOffering? toolOffering) ? toolOffering : null
            })
        ];
        List<Records.WorkflowDefinition> candidateWorkflows =
        [
            .. interviewState.Agent.Workflows.Select(workflow => DraftProjection.ToWorkflowDefinition(workflow) with
            {
                Offering = workflow.Name is not null && candidateOfferings.TryGetValue(workflow.Name, out Records.ToolOffering? workflowOffering) ? workflowOffering : null
            })
        ];

        // Morgana's own menu check runs here so the refusal at startup is heard while it costs nothing to change.
        List<string> refusals = HandlesIntentAgentRegistryService.ValidateOfferings(interviewState.Agent.ID ?? string.Empty, candidateTools, candidateWorkflows);
        if (refusals.Count > 0)
            return ToolReply.Refused(Said("MenuRefused", ("reasons", string.Join("; ", refusals))));

        // Only a menu that holds replaces the previous one.
        ApplyOfferings(candidateOfferings);

        // The reply reads the menu back in button order.
        return new ToolReply(Said("MenuRecorded", ("entries", string.Join("; ", candidateOfferings.Select(candidate =>
            $"{candidate.Value.Ordinal.ToString(CultureInfo.InvariantCulture)}. {candidate.Key} '{candidate.Value.Label}'")))));
    }

    /// <summary>
    /// Replaces the offering of every tool and workflow of the agent in hand with the one that the menu gives it, none for the rest.
    /// </summary>
    private void ApplyOfferings(Dictionary<string, Records.ToolOffering> offeringsByEntry)
    {
        // Every tool is rewritten so an entry dropped from the menu loses its button.
        foreach (ToolDraft tool in interviewState.Agent.Tools)
            tool.Offering = tool.Name is not null && offeringsByEntry.TryGetValue(tool.Name, out Records.ToolOffering? toolOffering) ? toolOffering : null;

        // Workflows follow the same rule as tools.
        foreach (WorkflowDraft workflow in interviewState.Agent.Workflows)
            workflow.Offering = workflow.Name is not null && offeringsByEntry.TryGetValue(workflow.Name, out Records.ToolOffering? workflowOffering) ? workflowOffering : null;
    }

    /// <summary>
    /// Opens a workflow, or revises the description of one already open.
    /// </summary>
    /// <remarks>Revising keeps the steps: a workflow is settled over several turns and re-declaring it must not empty it.</remarks>
    [Description("Opens a workflow, or revises the description of one already open — revising keeps its steps.")]
    [RequiresApproval(false)]
    public ToolReply DeclareWorkflow(
        [Description("PascalCase, no spaces or punctuation (e.g. 'PlaceOrder'). The agent's model starts the workflow by this name.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("What the procedure achieves for the person, in one sentence. The agent's model reads it to decide when to start this workflow.")] [ToolParameter(Records.ToolScope.Request)] string description)
    {
        // A blank name would leave the launcher function without a name.
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("WorkflowNameMissing"));

        // The name is what the launcher function is named after, so a workflow kept under one that has
        // to change would survive the corrected call as a second workflow.
        string complaint = IdentifierComplaint(alembicPromptService, cleanName, "workflow name", pascalCase: true);

        // A name off the convention is refused unrecorded, so the corrected call revises nothing it should have replaced.
        if (complaint.Length > 0)
            return ToolReply.Refused(Said("WorkflowNotRecorded") + " " + complaint);

        // A workflow of the same name on the agent is revised in place, otherwise a new authored one is opened.
        WorkflowDraft? match = FindWorkflow(cleanName);
        WorkflowDraft workflow = match ?? new WorkflowDraft { Name = cleanName, Origin = Provenance.Authored };

        // The description is what the model reads to decide when to start the workflow.
        workflow.Description = description?.Trim();

        // Only a new workflow joins the agent's list.
        if (match is null)
            interviewState.Agent.Workflows.Add(workflow);

        // The reply says whether the workflow was opened or revised and flags a missing description.
        return new ToolReply((match is not null ? Said("WorkflowRevised", ("name", cleanName)) : Said("WorkflowDeclared", ("name", cleanName)))
               + (string.IsNullOrWhiteSpace(description) ? " " + Said("WorkflowDescriptionMissing") : string.Empty));
    }

    /// <summary>
    /// Adds a step at the end of a workflow, or revises the tools of one already there by name and in place.
    /// </summary>
    /// <remarks>Revision keeps the step's place and its edges: <see cref="GetFindings"/> reports an edge whose tool the step no longer offers.</remarks>
    /// <param name="workflow">The already-declared workflow this step belongs to.</param>
    /// <param name="name">PascalCase, a short name for the moment of the procedure.</param>
    /// <param name="tools">The tools offered at this step, by their exact names.</param>
    [Description("Adds a step to a workflow, or revises the tools of one already there by name and in place. The first step set is where the workflow starts.")]
    [RequiresApproval(false)]
    public ToolReply SetWorkflowStep(
        [Description("The exact name of a workflow you have already declared.")] [ToolParameter(Records.ToolScope.Request)] string workflow,
        [Description("PascalCase, a short name for the moment of the procedure (e.g. 'Quote', 'Decide').")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("The tools offered at this step, by their exact names. Two or more make the step a choice the person makes.")] [ToolParameter(Records.ToolScope.Request)] string[] tools)
    {
        // A step belongs to a workflow declared earlier in the pass.
        if (FindWorkflow(workflow) is not { } owner)
            return ToolReply.Refused(Said("StepWorkflowMissing", ("workflow", workflow ?? string.Empty)));

        // A blank name would leave the step unreachable by the edges.
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("StepNameMissing"));

        // The tools offered at the step, without blanks or repeats.
        List<string> stepTools = [.. (tools ?? []).Select(tool => tool?.Trim() ?? string.Empty)
                                                  .Where(tool => tool.Length > 0)
                                                  .Distinct(StringComparer.Ordinal)];
        // Everything that makes the step unrecordable is gathered so that one refusal tells the model all of it.
        List<string> complaints = [];

        // A step name off the convention is refused.
        string identifierComplaint = IdentifierComplaint(alembicPromptService, cleanName, "step name", pascalCase: true);

        if (identifierComplaint.Length > 0)
            complaints.Add(identifierComplaint);

        // An agent that acquires tools from an MCP server names tools nothing here can see, so only
        // an agent with none of those can have a tool judged absent on the spot.
        if (interviewState.Agent.Code.MCPServers.Count == 0)
            foreach (string tool in stepTools.Where(tool => Find(tool) is null))
                complaints.Add(Said("StepToolUnknown", ("tool", tool)));

        // A step offering no tool gives the agent nothing to do at that moment.
        if (stepTools.Count == 0)
            complaints.Add(Said("StepNeedsTool"));

        // Nothing is recorded while any complaint stands.
        if (complaints.Count > 0)
            return ToolReply.Refused(Said("StepNotRecorded") + " " + string.Join(" ", complaints) + " " + Said("StepCorrect"));

        // A step of the same name is replaced in place, otherwise the new step closes the procedure so far.
        Records.WorkflowStep stepRecord = new(cleanName, stepTools);
        int position = owner.Steps.FindIndex(step => string.Equals(step.Name, cleanName, StringComparison.Ordinal));

        if (position >= 0)
            owner.Steps[position] = stepRecord;
        else
            owner.Steps.Add(stepRecord);

        // The reply says whether the step was revised or added at its position.
        return new ToolReply(position >= 0
            ? Said("StepRevised", ("name", cleanName), ("workflow", owner.Name ?? string.Empty))
            : Said("StepAdded", ("name", cleanName), ("workflow", owner.Name ?? string.Empty), ("position", owner.Steps.Count.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>
    /// Leads the successful call of a tool to the step that follows it, or revises where it leads.
    /// </summary>
    [Description("Leads the successful call of a tool to the step that follows it, or revises where it leads. Carries to that step the values that the call hands back.")]
    [RequiresApproval(false)]
    public ToolReply AddEdge(
        [Description("The exact name of a workflow you have already declared.")] [ToolParameter(Records.ToolScope.Request)] string workflow,
        [Description("The exact name of the step that offers the tool.")] [ToolParameter(Records.ToolScope.Request)] string source,
        [Description("The exact name of the step that the successful call leads to; it may be the same step.")] [ToolParameter(Records.ToolScope.Request)] string target,
        [Description("The exact name of the tool whose call this edge follows.")] [ToolParameter(Records.ToolScope.Request)] string tool,
        [Description("Names of the tool's own returned fields whose values the next step takes, each through a parameter of the same name. The agent never asks for them.")] [ToolParameter(Records.ToolScope.Request)] string[]? carrying = null)
        => RecordEdge(workflow, source, target, tool, carrying, onFailure: false);

    /// <summary>
    /// Leads the failed call of a tool to another step, or revises where it leads.
    /// </summary>
    [Description("Leads the failed call of a tool to another step, usually the same one to try again, or revises where it leads. A tool whose failed call no edge leads anywhere ends the workflow when it fails.")]
    [RequiresApproval(false)]
    public ToolReply AddFailureEdge(
        [Description("The exact name of a workflow you have already declared.")] [ToolParameter(Records.ToolScope.Request)] string workflow,
        [Description("The exact name of the step that offers the tool.")] [ToolParameter(Records.ToolScope.Request)] string source,
        [Description("The exact name of the step that the failed call leads to; usually the same step.")] [ToolParameter(Records.ToolScope.Request)] string target,
        [Description("The exact name of the tool whose call this edge follows.")] [ToolParameter(Records.ToolScope.Request)] string tool,
        [Description("Names of the tool's own returned fields whose values the next step takes, each through a parameter of the same name. The agent never asks for them.")] [ToolParameter(Records.ToolScope.Request)] string[]? carrying = null)
        => RecordEdge(workflow, source, target, tool, carrying, onFailure: true);

    /// <summary>
    /// Removes the edge that a tool's call follows.
    /// </summary>
    [Description("Removes the edge that a tool's call follows, so that the call ends the workflow.")]
    [RequiresApproval(false)]
    public ToolReply DropEdge(
        [Description("The exact name of a workflow you have already declared.")] [ToolParameter(Records.ToolScope.Request)] string workflow,
        [Description("The exact name of the step that offers the tool.")] [ToolParameter(Records.ToolScope.Request)] string source,
        [Description("The exact name of the tool whose edge is removed.")] [ToolParameter(Records.ToolScope.Request)] string tool,
        [Description("True for the edge of a failed call, false for the edge of a successful one.")] [ToolParameter(Records.ToolScope.Request)] bool failure)
    {
        // An edge can only be dropped from a workflow the pass has declared.
        if (FindWorkflow(workflow) is not { } owner)
            return ToolReply.Refused(Said("DropWorkflowUnknown", ("workflow", workflow ?? string.Empty)));

        // The edge is keyed by source step and tool and outcome. The count says whether it was there.
        int removals = owner.Edges.RemoveAll(edge => IsEdge(edge, source, tool, failure));
        string edgeKind = Said(failure ? "EdgeKindFailure" : "EdgeKindSuccess");

        // A removal is confirmed while an edge that was never there is refused.
        return removals > 0
            ? new ToolReply(Said("EdgeDropped",
                ("edge", edgeKind), ("tool", tool ?? string.Empty), ("source", source ?? string.Empty), ("workflow", owner.Name ?? string.Empty)))
            : ToolReply.Refused(Said("EdgeAbsent",
                ("workflow", owner.Name ?? string.Empty), ("edge", edgeKind), ("tool", tool ?? string.Empty), ("source", source ?? string.Empty)));
    }

    /// <summary>
    /// Records an edge under the key (source, tool, outcome), replacing the one already held there in place.
    /// </summary>
    /// <remarks>Only what makes the edge unrecordable is refused: whether the carried names are returned by the tool is left to <see cref="GetFindings"/>.</remarks>
    private ToolReply RecordEdge(string workflow, string source, string target, string tool, string[]? carrying, bool onFailure)
    {
        // An edge belongs to a workflow declared earlier in the pass.
        if (FindWorkflow(workflow) is not { } owner)
            return ToolReply.Refused(Said("EdgeWorkflowMissing", ("workflow", workflow ?? string.Empty)));

        // The names the model gave for the edge's two ends and its tool.
        string cleanSource = (source ?? string.Empty).Trim();
        string cleanTarget = (target ?? string.Empty).Trim();
        string cleanTool = (tool ?? string.Empty).Trim();
        // Everything that makes the edge unrecordable is gathered so that one refusal tells the model all of it.
        List<string> complaints = [];

        // The step the call is made at, whose offered tools decide whether the edge can follow.
        Records.WorkflowStep? sourceStep = owner.Steps.FirstOrDefault(step => string.Equals(step.Name, cleanSource, StringComparison.Ordinal));

        // An edge cannot leave a step that does not exist.
        if (sourceStep is null)
            complaints.Add(Said("EdgeNoSuchStep", ("workflow", owner.Name ?? string.Empty), ("step", source ?? string.Empty)));
        // An edge follows only a tool that its source step offers.
        else if (!sourceStep.Tools.Contains(cleanTool, StringComparer.Ordinal))
            complaints.Add(Said("EdgeToolNotOffered", ("tool", cleanTool), ("step", cleanSource)));

        // An edge cannot lead to a step that does not exist.
        if (!owner.Steps.Any(step => string.Equals(step.Name, cleanTarget, StringComparison.Ordinal)))
            complaints.Add(Said("EdgeNoSuchStep", ("workflow", owner.Name ?? string.Empty), ("step", target ?? string.Empty)));

        // Nothing is recorded while any complaint stands.
        if (complaints.Count > 0)
            return ToolReply.Refused(Said("EdgeNotRecorded") + " " + string.Join(" ", complaints) + " " + Said("EdgeCorrect"));

        // The values the next step takes from the call's result, without blanks or repeats.
        List<string> cargo = [.. (carrying ?? []).Select(name => name?.Trim() ?? string.Empty)
                                                   .Where(name => name.Length > 0)
                                                   .Distinct(StringComparer.Ordinal)];
        // A call has one edge per outcome: an existing one is replaced in place, otherwise the edge is added.
        Records.WorkflowEdge edgeRecord = new(cleanSource, cleanTarget, cleanTool, onFailure, cargo);
        int position = owner.Edges.FindIndex(edge => IsEdge(edge, cleanSource, cleanTool, onFailure));

        if (position >= 0)
            owner.Edges[position] = edgeRecord;
        else
            owner.Edges.Add(edgeRecord);

        // The reply says whether the edge was added or revised and where it leads.
        return new ToolReply(Said("EdgeRecorded",
            ("edge", Said(onFailure ? "EdgeKindFailure" : "EdgeKindSuccess")),
            ("tool", cleanTool),
            ("source", cleanSource),
            ("outcome", Said(position >= 0 ? "EdgeOutcomeRevised" : "EdgeOutcomeAdded")),
            ("workflow", owner.Name ?? string.Empty),
            ("target", cleanTarget)));
    }

    /// <summary>
    /// Whether an edge is the one keyed by the source step, the tool and the outcome.
    /// </summary>
    private static bool IsEdge(Records.WorkflowEdge edge, string? source, string? tool, bool onFailure) =>
        edge.OnFailure == onFailure
        && string.Equals(edge.Source, source?.Trim(), StringComparison.Ordinal)
        && string.Equals(edge.Tool, tool?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Removes a step from a workflow with every edge that leaves it or leads to it.
    /// </summary>
    [Description("Removes a step from a workflow with every edge that leaves it or leads to it.")]
    [RequiresApproval(false)]
    public ToolReply DropWorkflowStep(
        [Description("The exact name of the workflow.")] [ToolParameter(Records.ToolScope.Request)] string workflow,
        [Description("The exact name of the step to remove.")] [ToolParameter(Records.ToolScope.Request)] string step)
    {
        // A step can only be dropped from a workflow the pass has declared.
        if (FindWorkflow(workflow) is not { } owner)
            return ToolReply.Refused(Said("DropWorkflowUnknown", ("workflow", workflow ?? string.Empty)));

        // The step is dropped by exact name and the count says whether it was there.
        string cleanStep = step?.Trim() ?? string.Empty;
        int removals = owner.Steps.RemoveAll(candidate => string.Equals(candidate.Name, cleanStep, StringComparison.Ordinal));

        // A name that was never a step of the workflow is refused.
        if (removals == 0)
            return ToolReply.Refused(Said("StepAbsent", ("workflow", owner.Name ?? string.Empty), ("step", step ?? string.Empty)));

        // An edge whose end is gone would name a step that no longer exists.
        int edgesRemoved = owner.Edges.RemoveAll(edge =>
            string.Equals(edge.Source, cleanStep, StringComparison.Ordinal) || string.Equals(edge.Target, cleanStep, StringComparison.Ordinal));

        // The reply says how many edges went with the step.
        return new ToolReply(edgesRemoved > 0
            ? Said("StepDroppedWithEdges", ("step", step ?? string.Empty), ("workflow", owner.Name ?? string.Empty), ("count", edgesRemoved.ToString(CultureInfo.InvariantCulture)))
            : Said("StepDropped", ("step", step ?? string.Empty), ("workflow", owner.Name ?? string.Empty)));
    }

    /// <summary>
    /// Removes a workflow and every step of it.
    /// </summary>
    [Description("Removes a workflow and every step of it.")]
    [RequiresApproval(false)]
    public ToolReply DropWorkflow(
        [Description("The exact name of the workflow to remove.")] [ToolParameter(Records.ToolScope.Request)] string name)
    {
        // The workflow is dropped by exact name and the count says whether it was on the agent.
        int removals = interviewState.Agent.Workflows.RemoveAll(workflow =>
            string.Equals(workflow.Name, name?.Trim(), StringComparison.Ordinal));

        // A removal is confirmed while a name that was never there is refused.
        return removals > 0
            ? new ToolReply(Said("WorkflowDropped", ("name", name ?? string.Empty)))
            : ToolReply.Refused(Said("DropWorkflowUnknown", ("workflow", name ?? string.Empty)));
    }

    /// <summary>
    /// Returns the workflows as they currently stand.
    /// </summary>
    [Description("Returns the agent's workflows as they stand, step by step. Call it before completing the pass.")]
    [RequiresApproval(false)]
    public ToolReply GetWorkflows() => new ToolReply(DescribeWorkflows(alembicPromptService, interviewState.Agent.Workflows));

    /// <summary>
    /// Renders workflows as readable text: one block each, its steps in order with the tools, then one
    /// line per edge with the values it carries.
    /// </summary>
    /// <remarks>Internal because the coherence passes read the workflows in the interview's own words.</remarks>
    /// <param name="workflows">The workflows to render.</param>
    internal static string DescribeWorkflows(IAlembicPromptService alembicPromptService, IReadOnlyList<WorkflowDraft> workflows)
    {
        // An agent without workflows is told so in words: an empty list says nothing to the model.
        if (workflows.Count == 0)
            return alembicPromptService.ResolveToolInjection("WorkflowsNone");

        // Each workflow is rendered with its steps in order and the edges that connect them.
        return alembicPromptService.ResolveToolInjection("WorkflowsHeader") + "\n\n"
               + string.Join("\n\n", workflows.Select(workflow =>
                   $"Workflow {workflow.Name ?? "(unnamed)"}{DescribeOffering(alembicPromptService, workflow.Offering)}: {workflow.Description ?? "(no description)"}"
                   + (workflow.Steps.Count == 0
                       ? "\n  (no step yet)"
                       : string.Concat(workflow.Steps.Select((step, position) =>
                           $"\n  {position + 1}. {step.Name} — tools: "
                           + (step.Tools.Count == 0 ? "(none yet)" : string.Join(", ", step.Tools)))))
                   + string.Concat(workflow.Edges.Select(edge => "\n  " + DescribeEdge(edge)))));
    }

    /// <summary>
    /// Renders the bracketed note that says a tool or a workflow stands on the agent's menu, empty for one that does not.
    /// </summary>
    internal static string DescribeOffering(IAlembicPromptService alembicPromptService, Records.ToolOffering? offering) =>
        // Only an offered entry carries the note: the model reads the menu back beside the head line of what it offers.
        offering is null
            ? string.Empty
            : " [" + alembicPromptService.ResolveToolInjection("OnMenu",
                ("ordinal", offering.Ordinal.ToString(CultureInfo.InvariantCulture)),
                ("label", offering.Label)) + "]";

    /// <summary>
    /// Renders one edge as a line saying where the call leads and the values it carries.
    /// </summary>
    internal static string DescribeEdge(Records.WorkflowEdge edge) =>
        $"{edge.Source} --{edge.Tool}{(edge.OnFailure ? " (failure)" : string.Empty)}--> {edge.Target}"
        + (edge.Carrying is { Count: > 0 } ? " carrying " + string.Join(", ", edge.Carrying) : string.Empty);

    /// <summary>
    /// Finds a declared workflow by exact name, ordinal because the framework looks it up that way.
    /// </summary>
    private WorkflowDraft? FindWorkflow(string? workflowName) =>
        interviewState.Agent.Workflows.FirstOrDefault(workflow =>
            string.Equals(workflow.Name, workflowName?.Trim(), StringComparison.Ordinal));

    /// <summary>
    /// Returns what earlier passes settled about this agent.
    /// </summary>
    /// <remarks>Each pass starts with a fresh session, so the configuration this returns is all that carries over from earlier passes.</remarks>
    [Description("Returns the agent as it currently stands: the intent it answers, what it is for, where it stops, how it sounds and what it can reach. Each step runs with a fresh memory and only the configuration carries over, so this is the whole of what you know about it. On an agent already in the client's domain, all of it was written before today.")]
    [RequiresApproval(false)]
    public ToolReply GetAgentSoFar()
    {
        // Without an intent on the map there is no agent to describe yet.
        if (string.IsNullOrWhiteSpace(interviewState.Intent.Name))
            return new ToolReply(Said("AgentNoIntent"));

        // The intent, the sentence that opens it and the two sections settled first are always shown, a missing one flagged.
        List<string> sections =
        [
            $"Intent '{interviewState.Intent.Name}': {interviewState.Intent.Description}",
            $"Opening sentence a user would send: {interviewState.Intent.DefaultValue}",
            Records.Prompt.Labeled(Constants.SectionLabels.Target, interviewState.Agent.Target ?? "(no target)"),
            Records.Prompt.Labeled(Constants.SectionLabels.Personality, interviewState.Agent.Personality ?? "(no personality)")
        ];

        // The Instructions appear once a pass has written them.
        if (!string.IsNullOrWhiteSpace(interviewState.Agent.Instructions))
            sections.Add(Records.Prompt.Labeled(Constants.SectionLabels.Instructions, interviewState.Agent.Instructions));

        // The Formatting appears once the last pass has written it.
        if (!string.IsNullOrWhiteSpace(interviewState.Agent.Formatting))
            sections.Add(Records.Prompt.Labeled(Constants.SectionLabels.Formatting, interviewState.Agent.Formatting));

        // The workflows appear once the agent has any.
        if (interviewState.Agent.Workflows.Count > 0)
            sections.Add(DescribeWorkflows(alembicPromptService, interviewState.Agent.Workflows));

        // The reply hands the pass everything settled about this agent so far.
        return new ToolReply(Said("AgentSoFarHeader") + "\n\n"
               + string.Join("\n\n", sections));
    }

    /// <summary>
    /// Offers words for the agent's voice, to be picked from freely.
    /// </summary>
    /// <remarks>Traits are taken several at a time and none is an answer on its own, which is why they are not buttons.</remarks>
    [Description("Offers words for this agent's voice under the question you are about to ask, to be picked from freely — several, one, or none and the text box stays open beside them. Call it before writing the question. Every word must be a way Morgana herself could speak: this section specialises her voice and never replaces it.")]
    [RequiresApproval(false)]
    public ToolReply SetTraits(
        [Description("Eight to fourteen single adjectives, in the client's language. Real alternatives rather than shades of one temper, each plausible for this domain and none of them a way Morgana would never speak.")] [ToolParameter(Records.ToolScope.Request)] List<string> traits)
    {
        // Blank entries would draw empty chips under the question.
        List<string> words = [.. traits.Select(trait => trait.Trim()).Where(trait => trait.Length > 0)];

        // A list with no word offers the client nothing to pick from.
        if (words.Count == 0)
            return ToolReply.Refused(Said("TraitsNone"));

        // The words replace any earlier offer and are drawn under the next question.
        interviewState.PendingTraits.Clear();
        interviewState.PendingTraits.AddRange(words);

        // The reply says how many words will be drawn.
        return new ToolReply(Said("TraitsDrawn", ("count", words.Count.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>The number of facts one agent or the business may hold on record before the pass has to tidy them.</summary>
    /// <remarks>Every step opens holding the record of the agent in hand, so a record that grows without end is read in full to ask one question.</remarks>
    private const int MemoryCeiling = 14;

    /// <summary>
    /// The record that receives what the client says: the agent in hand while standing on an entry of the map or the business otherwise.
    /// </summary>
    /// <remarks>The map pass holds no agent, so what it learns is about the business or it is lost with the empty agent.</remarks>
    private List<KnownFact> Memory => interviewState.OnAnEntry
        ? interviewState.Agent.Known
        : (draftStateService.Current ?? new DomainDraft()).Learned;

    /// <summary>
    /// Writes down one thing the client has just said about how their work actually goes.
    /// </summary>
    /// <remarks>What the client says about their trade is spent when the turn ends unless it is written down here for the steps that follow.</remarks>
    /// <param name="subject">What it is about, in one or two of the client's own words.</param>
    /// <param name="fact">The fact itself, one sentence, in their vocabulary.</param>
    /// <param name="corrects">Any part of the sentence on record that the client has just contradicted: it is removed so the truth does not sit beside it.</param>
    [Description("Writes down one thing the client has just told you about how their work actually goes — not what you decided about it. Every step after this one opens holding what is on record for the agent in hand and for the business, so call it the moment an answer says something a later step would otherwise have to ask again: what they sell, who writes in and about what, what they open to answer it, what goes wrong, what they never do. One fact per call, in their own words. Never what you wrote into a section — that is already in the configuration and a second copy of it here is a second thing to keep true. Where what they just said contradicts something on record, say so in 'corrects' and the wrong one goes: a record nobody can correct is worse than no record, because every step after you will hold it and none of them can doubt it.")]
    [RequiresApproval(false)]
    public ToolReply NoteDomainFact(
        [Description("What this is about, in one or two of their words — 'prices', 'deposits', 'custom cakes'. Facts about the same thing take the same subject: it is what another agent's step reads to decide whether to fetch this at all.")] [ToolParameter(Records.ToolScope.Request)] string subject,
        [Description("One short sentence about their work, in their vocabulary, never about the agent: 'the catalogue changes with the season', 'custom cakes are quoted at an appointment, never over the counter'. Standing on one entry of the map it is kept with that agent; drawing the map or closing the domain it is kept for the business as a whole.")] [ToolParameter(Records.ToolScope.Request)] string fact,
        [Description("Any part of the sentence already on record that this puts right, where they have just contradicted it — a reading taken off their upload most of all. Leave it out when nothing is being corrected.")] [ToolParameter(Records.ToolScope.Request)] string? corrects = null)
    {
        // A fact is a subject with a sentence about it.
        string sentence = fact.Trim();
        string topic = subject.Trim();

        // A fact missing either half would file nothing a later step could find.
        if (sentence.Length == 0 || topic.Length == 0)
            return ToolReply.Refused(Said("FactIncomplete"));

        // The facts go to the agent in hand or to the business as the interview stands.
        List<KnownFact> facts = Memory;

        // What the client has just contradicted leaves the record first.
        int removals = corrects is { Length: > 0 } mistake ? Forget(facts, mistake) : 0;

        // What the client says stands over what Alembic read off their upload about the same
        // subject: a reading of somebody else's prose loses to the person whose shop it is.
        removals += facts.RemoveAll(entry =>
            entry.Inferred && string.Equals(entry.Subject, topic, StringComparison.OrdinalIgnoreCase));

        // A sentence already on record is not written twice.
        if (facts.Any(entry => string.Equals(entry.Fact, sentence, StringComparison.OrdinalIgnoreCase)))
            return new ToolReply(Said("FactDuplicate"));

        // A full record is refused so the pass merges facts before adding more.
        if (facts.Count >= MemoryCeiling)
            return ToolReply.Refused(Said("FactMemoryFull", ("count", facts.Count.ToString(CultureInfo.InvariantCulture))));

        // The client said it, so the fact is not marked as inferred.
        facts.Add(new KnownFact(topic, sentence, Inferred: false));

        // The reply says what was written and how many readings it replaced.
        return new ToolReply(Said("FactWritten",
            ("subject", topic),
            ("replaced", removals > 0 ? Said("FactReplaced", ("count", removals.ToString(CultureInfo.InvariantCulture))) : string.Empty),
            ("count", facts.Count.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>
    /// Takes something off the record that turned out not to be true of their work.
    /// </summary>
    /// <remarks>A wrong fact left on record is held by every later step and none of them can doubt it.</remarks>
    /// <param name="fact">Any part of the sentence to remove, enough to tell it from the others.</param>
    [Description("Takes something off the record that is not true of their work. Use it the moment the client says otherwise. Use it on your own readings of their upload without hesitation — nobody confirmed those. Left standing, a wrong fact is held by every step after this one and none of them has any way to doubt it.")]
    [RequiresApproval(false)]
    public ToolReply DropDomainFact(
        [Description("Any part of the sentence to remove, or its subject, enough to tell it from the others on record.")] [ToolParameter(Records.ToolScope.Request)] string fact)
    {
        // The text that identifies the wrong fact.
        string mistake = fact.Trim();

        // An empty text would match every fact on record.
        if (mistake.Length == 0)
            return ToolReply.Refused(Said("FactDropEmpty"));

        // The wrong fact is removed from the record of the agent in hand or of the business.
        int removals = Forget(Memory, mistake);

        // A text matching nothing on record is refused.
        return removals == 0
            ? ToolReply.Refused(Said("FactAbsent"))
            : new ToolReply(Said("FactsDropped", ("count", removals.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>
    /// Hands back what is on record about one of the other agents of this domain.
    /// </summary>
    /// <remarks>A step opens holding only the subjects of the other agents: this reaches the rest when one of them bears on the question in hand.</remarks>
    /// <param name="intent">The agent's own intent name, as the opening message lists it.</param>
    [Description("Hands back what is on record about one of the other agents of this domain. You open holding what is known about the agent in hand and about the business; for every other agent you hold only the subjects it keeps — a domain read whole would be forty sentences carried into every question, nearly all about counters this step will never touch. Call this when a subject listed against another agent bears on the question you are about to ask: whether the counter next door already takes deposits decides whether this one should.")]
    [RequiresApproval(false)]
    public ToolReply RecallAgent(
        [Description("The agent's own intent name, exactly as the opening message lists it.")] [ToolParameter(Records.ToolScope.Request)] string intent)
    {
        // The intent name the opening message listed for the agent asked about.
        string intentName = intent.Trim();

        // The domain as committed so far, empty when nothing has been saved yet.
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // An agent of this domain is an entry of the map, written or still ahead: the universe the
        // opening message lists its neighbours from.
        IntentDraft? entry = draft.Intents.Concat(interviewState.Map).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, intentName, StringComparison.OrdinalIgnoreCase));

        // The written agent holding the facts, absent while the interview has not reached it.
        AgentDraft? agent = draft.Agents.FirstOrDefault(agent =>
            string.Equals(agent.ID, intentName, StringComparison.OrdinalIgnoreCase));

        // A name that is neither on the map nor in the domain is refused.
        if (entry is null && agent is null)
            return ToolReply.Refused(Said("RecallUnknown", ("name", intentName)));

        // For an agent nobody has opened yet the routing sentence the client dictated is the only account of it.
        if (agent is null || agent.Known.Count == 0)
            return new ToolReply(string.IsNullOrWhiteSpace(entry?.Description)
                ? Said("RecallNothing", ("name", intentName))
                : Said("RecallMapOnly", ("name", intentName), ("description", entry.Description)));

        // The facts on record are listed by subject and each reading taken off the upload is marked as such.
        return new ToolReply(Said("RecallHeader", ("name", intentName)) + "\n"
               + string.Join("\n", agent.Known.Select(entry =>
                   $"- {entry.Subject}: {entry.Fact}" + (entry.Inferred ? " " + Said("RecalledInferred") : string.Empty))));
    }

    /// <summary>
    /// Removes the facts on record that match the wrong sentence by wording or by subject and returns how many went.
    /// </summary>
    private static int Forget(List<KnownFact> facts, string mistake) =>
        facts.RemoveAll(entry =>
            entry.Fact.Contains(mistake, StringComparison.OrdinalIgnoreCase)
            || mistake.Contains(entry.Fact, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Subject, mistake, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Says what the step about to be asked adds to the agent in hand, over its question.
    /// </summary>
    /// <remarks>It belongs to the turn a step lands on and to no confirmation or follow-up after it.</remarks>
    [Description("States, over the question you are about to ask, what this step adds to the agent in hand. Call it in the turn a step lands on, before the question, every time without exception: a step that lands with nothing but a question leaves the client working out what this screen is for from the question alone. Call it in no other turn — a confirmation or a follow-up is not a landing and what stands there would say the interview had moved on when it has not. It is not room for anything else: never an answer to what they just said, never an apology, never a correction of your own last turn. Those belong in the question.")]
    [RequiresApproval(false)]
    public ToolReply SetStepPlacing(
        [Description("One short sentence in the second person, naming what they will be able to do once this step is settled, in the things their work is actually made of — the orders, the counter, the people who write in. Name neither the agent nor the step: both already stand lit on the screen. Where the sentence needs a subject it is the part of their work this agent stands for, in their own words. Two things it is never: a line about what software becomes able to do ('this is where the catalogue stops being just words and becomes something you can really consult' is a brochure and tells a baker nothing) or a restatement of why this stage exists, which is written above for you and not for them. The test is simple: if the same sentence could open this step for a garage, a vet and a bank alike, it places nothing. Never place it at the expense of the steps behind it: what those settled is already their configuration, so a step announcing that things now finally become real tells them the screens they have just filled in were a game.")] [ToolParameter(Records.ToolScope.Request)] string placing)
    {
        // The sentence the client reads over the question.
        string placingText = placing.Trim();

        // An empty sentence would draw a blank line over the question.
        if (placingText.Length == 0)
            return ToolReply.Refused(Said("PlacingEmpty"));

        // The screen draws the sentence apart from the question in its own voice.
        interviewState.PendingPlacing = placingText;

        // The reply tells the model the placing is set and the question may follow.
        return new ToolReply(Said("PlacingSet"));
    }

    /// <summary>
    /// Shows the client, word for word, the prose just written for their agent.
    /// </summary>
    /// <remarks>The client approves exact words, so they stand apart from the sentence introducing them.</remarks>
    /// <param name="written">The section's prose exactly as it now stands, with nothing added around it.</param>
    [Description("Shows the client, word for word, the prose you have just written for their agent. Call it on the turn that asks whether a section is right, before the question: what they are approving is those exact words, so those words stand on their own and your own sentence introduces them rather than containing them. 'I have written a fond and careful voice that follows…' is one stretch of prose in which nothing marks where you stop speaking and their agent's text begins; an approval given to that approves nothing in particular. Only what is actually written — never a paraphrase, never a summary of it.")]
    [RequiresApproval(false)]
    public ToolReply ShowWhatIsWritten(
        [Description("The section's prose exactly as it now stands, with nothing added around it and no quotation marks of your own.")] [ToolParameter(Records.ToolScope.Request)] string written)
    {
        // The prose exactly as written for the agent.
        string prose = written.Trim();

        // An empty quotation would show the client nothing to approve.
        if (prose.Length == 0)
            return ToolReply.Refused(Said("QuotedEmpty"));

        // The screen draws the prose apart from Alembic's own sentence.
        interviewState.PendingQuoted = prose;

        // The reply tells the model the quotation is set and the question may follow.
        return new ToolReply(Said("QuotedSet"));
    }

    /// <summary>
    /// Puts a worked example in the answer box under the question about to be asked.
    /// </summary>
    /// <remarks>It teaches register and length to a first-time client and is written per question, so it reads as an answer from the client's own trade.</remarks>
    [Description("Puts a worked example in the answer box under the question you are about to ask, greyed out, for them to take and cut about or to type over.")]
    [RequiresApproval(false)]
    public ToolReply SetExample(
        [Description("An answer somebody in their trade might have given to the question you are about to ask, cut to the size of that question — forty to sixty words where it opens a step, ten to twenty where it follows one up — in their vocabulary and about their own work — never about another business, never a description of what to write and never the question itself put into their mouth: an example that says what the question says adds nothing to the screen it stands on. It teaches the register, the length and the level of detail at once. Send it unquoted: it stands in the box as text they can send as it is, so quotation marks around it arrive as part of their own answer. Make it close enough to their work to be worth editing and specific enough that it cannot be agreed with as it stands: an example that fits them exactly is one they will press past without adding what only they know.")] [ToolParameter(Records.ToolScope.Request)] string example)
    {
        // The box holds it as the client's own text, so quotation marks around it would be sent as part of the answer.
        string exampleText = example.Trim().Trim('"').Trim();

        // An empty example would leave the answer box blank.
        if (exampleText.Length == 0)
            return ToolReply.Refused(Said("ExampleEmpty"));

        // The example is drawn greyed out in the answer box under the question.
        interviewState.PendingExample = exampleText;

        // The reply tells the model the example is set and the question may follow.
        return new ToolReply(Said("ExampleSet"));
    }

    /// <summary>
    /// Attaches to the question about to be asked the one button that answers it without adding anything.
    /// </summary>
    /// <remarks>There is exactly one button because the answer that adds nothing is the only one Alembic knows the whole of.</remarks>
    [Description("Attaches to the question you are about to ask the one button that answers it without adding anything — agreement, or that there is nothing to add — so a client in that position settles the turn with one press instead of a typed sentence. Call it before writing the question: the button renders below it. The text box stays open either way.")]
    [RequiresApproval(false)]
    public ToolReply SetChoice(
        [Description("What the client reads on the button. It carries the answer that adds nothing and has to answer the question as you put it — 'That's everything' where you asked whether that is everything, 'Nothing missing' where you asked whether anything is missing, 'No, nothing like that' where you asked whether there is something the agent should never do.")] [ToolParameter(Records.ToolScope.Request)] string label,
        [Description("The complete answer the button sends, in the client's register, reading as something they would plausibly have typed.")] [ToolParameter(Records.ToolScope.Request)] string value)
    {
        // A button needs both the text the client reads and the answer it sends.
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
            return ToolReply.Refused(Said("ChoiceIncomplete"));

        // The single button is drawn below the question through the channels' own quick reply contract.
        interviewState.PendingChoice = new QuickReply("agree", label.Trim(), value.Trim());

        // The reply tells the model the button is set and the question may follow.
        return new ToolReply(Said("ChoiceSet"));
    }

    /// <summary>
    /// Returns the intents already in the domain.
    /// </summary>
    /// <remarks>A description only has to be told apart from both sources: the domain's committed intents and the entries the map still has to write.</remarks>
    [Description("Returns the name and description of every intent already in the domain, the descriptions a new one is weighed against.")]
    [RequiresApproval(false)]
    public ToolReply GetExistingIntents()
    {
        // Intents committed to the domain by earlier sittings or an upload, apart from the one in hand.
        IEnumerable<string> domainLines = (draftStateService.Current?.Intents ?? [])
            .Where(i => !string.Equals(i.Name, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase))
            .Select(i => $"- {i.Name}: {i.Description} {Said("IntentAlreadyInDomain")}");

        // Entries the map promised to write next, apart from the one in hand.
        IEnumerable<string> mapLines = interviewState.Map
            .Where(i => !ReferenceEquals(i, interviewState.Intent))
            .Select(i => $"- {i.Name}: {i.Description} {Said("IntentOnMapNotWritten")}");

        // Both sources are weighed together against the description being written.
        List<string> lines = [.. domainLines, .. mapLines];

        // The reply lists every other intent or says the domain holds none.
        return new ToolReply(lines.Count == 0
            ? Said("IntentsNone")
            : Said("IntentsHeader") + "\n" + string.Join("\n", lines));
    }

    /// <summary>
    /// Whether another agent of the draft declares it may consult this one, which the agent's own <c>Consults</c> list cannot show.
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
    [Description("Returns the complete prompt this agent's model will read, composed as Morgana composes it — her layer, the fences and your sections beneath. Reading your own work composed is the only way to see whether your layer contradicts hers or merely repeats her.")]
    [RequiresApproval(false)]
    public async Task<ToolReply> GetComposedPrompt()
    {
        // Without a Target the composed prompt would open on nothing.
        if (string.IsNullOrWhiteSpace(interviewState.Agent.Target))
            return new ToolReply(Said("ComposeNoTarget"));

        // The framework composes the prompt for the agent in hand, with the colleague policy when it asks or is asked by a colleague.
        AgentRecap recap = await recapService.ComposeAsync(
            interviewState.Agent,
            IsConsultedByOthers(interviewState.Agent));

        // Morgana's layer is identical for every agent and already sits in the pass's context after the first reading.
        // The first reading is whole since the comparison needs her words; later ones start at the agent's own Target.
        int domain = framework ? recap.SystemPrompt.LastIndexOf(Constants.SectionLabels.Target, StringComparison.Ordinal) : -1;

        // From now on this pass has read Morgana's layer.
        framework = true;

        // The reply carries the whole prompt the first time and only the agent's own sections after.
        return new ToolReply(domain < 0
            ? Said("ComposedWhole") + "\n\n" + recap.SystemPrompt
            : Said("ComposedYours") + "\n\n" + recap.SystemPrompt[domain..]);
    }

    /// <summary>
    /// Returns the card this agent will present to whoever might consult it.
    /// </summary>
    /// <remarks>A territory that reads as a list of functions or never left the routing phrase is visible here and nowhere else in the interview.</remarks>
    [Description("Returns the card this agent will present to anyone who might consult it: what it answers for and the skills it advertises. Call it once the territory is written — a colleague weighing a question reads that description and nothing else, so this is where a sentence that reads as a list of functions is visible.")]
    [RequiresApproval(false)]
    public ToolReply GetAgentCard()
    {
        // The card is packed as a caller will be handed it: the intent's name, the description as it stands and the tools as skills.
        return new ToolReply(Said("AgentCard",
            ("intent", interviewState.Intent.Name ?? string.Empty),
            ("description", CardProjection.Description(interviewState.Intent, interviewState.Agent)),
            ("skills", interviewState.Agent.Tools.Count == 0
                ? Said("AgentCardNoSkills")
                : CardProjection.Skills(interviewState.Agent))));
    }

    /// <summary>
    /// Rewrites the routing description of the intent in hand in the sharper words the territory settled.
    /// </summary>
    /// <remarks>Only this entry is written: every other description is read back first, which keeps this one distinct.</remarks>
    [Description("Rewrites what this entry routes on, in the sharper words the territory settled. The classifier reads it against every other entry's description, so read those back first and leave this one distinct from all of them. Only this entry: no other intent of the domain is yours.")]
    [RequiresApproval(false)]
    public ToolReply SetIntentDescription(
        [Description("What lands on this agent, said for the classifier: the subject and the kinds of request that belong to it, in the client's own words. Never the agent's boundaries, its tools or its voice.")] [ToolParameter(Records.ToolScope.Request)] string description)
    {
        // The sentence the classifier will route on from now on.
        string descriptionText = (description ?? string.Empty).Trim();

        // An empty description would leave the classifier with nothing to route on.
        if (descriptionText.Length == 0)
            return ToolReply.Refused(Said("IntentDescriptionEmpty"));

        // The intent in hand takes the sharper routing sentence.
        interviewState.Intent.Description = descriptionText;

        // The reply repeats what the entry now routes on.
        return new ToolReply(Said("IntentRoutesOn", ("name", interviewState.Intent.Name ?? string.Empty), ("description", descriptionText)));
    }

    /// <summary>
    /// Returns everything wrong with this agent that is decidable without a model.
    /// </summary>
    /// <remarks>Findings about the client's other agents are filtered out: they are real but this pass cannot fix them.</remarks>
    [Description("Returns everything wrong with this agent that can be decided without a model. Fix what it reports rather than explaining it away.")]
    [RequiresApproval(false)]
    public ToolReply GetFindings()
    {
        // Without an intent there is no agent to check.
        if (string.IsNullOrWhiteSpace(interviewState.Intent.Name))
            return new ToolReply(Said("FindingsNoIntent"));

        // The domain as committed so far, empty when nothing has been saved yet.
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // Relational rules (an intent nothing routes to or a name colliding with a framework prompt) need the client's domain plus the agent under construction.
        DomainDraft probe = new DomainDraft
        {
            Intents = [.. draft.Intents, interviewState.Intent],
            Agents = [.. draft.Agents, interviewState.Agent]
        };

        // The intent name that scopes the findings to this agent.
        string intentName = interviewState.Intent.Name!;

        // Only findings about this agent or the domain as a whole are this pass's business.
        List<ValidationFinding> findings =
            [.. draftValidationService.Validate(probe)
                .Where(f => f.Where.Contains($"'{intentName}'", StringComparison.OrdinalIgnoreCase)
                            || f.Where.StartsWith($"{intentName}.", StringComparison.OrdinalIgnoreCase)
                            || f.Where == "domain")];

        // The reply lists each finding with its severity and the rule it breaks; with none it says so.
        return new ToolReply(findings.Count == 0
            ? Said("FindingsNone")
            : string.Join("\n", findings.Select(f => $"[{f.Severity}] {f.Where}: {f.Message} — {f.Because}")));
    }

    /// <summary>
    /// Declares the pass settled.
    /// </summary>
    /// <remarks>The state machine confirms the claim: which fields are set is a fact and not the model's to assert.</remarks>
    [Description("Declares this pass settled. The declaration lives in this call and nowhere else: never write a marker, a token or a closing formula into your text to mean it.")]
    [RequiresApproval(false)]
    public ToolReply SetPassCompleted()
    {
        // When correcting, every section is already written, so a pass could settle the moment it opened and walk the client through an edit that asked nothing.
        // A pass may settle on the client's first word and never before it.
        if (interviewState.Revision is not null && interviewState.Exchanges == interviewState.PassOpenedAt)
            return ToolReply.Refused(Said("PassNotYetSaid"));

        // The fields this pass must have written, checked against the state.
        IReadOnlyList<string> gaps = interviewState.Missing();

        // A pass with fields still missing is refused and told which ones.
        if (gaps.Count > 0)
            return ToolReply.Refused(Said("PassIncomplete",
                ("missing", string.Join(", ", gaps)),
                ("advice", Said(gaps.Count == 1 ? "PassMissingOne" : "PassMissingMany"))));

        // The state machine confirmed the fields, so the interview may open the next pass.
        interviewState.ReadyForReview = true;

        // The model announces what comes next from the pass that has just closed.
        // Passes not listed close onto the agent joining the domain.
        string nextStep = interviewState.Pass switch
        {
            // The map closes onto the first entry of the list it just drew.
            InterviewStep.DomainMapper => Said("NextAfterMap", ("count", interviewState.Map.Count.ToString(CultureInfo.InvariantCulture))),
            // Each pass of one agent closes onto the one that follows it.
            InterviewStep.AgentTarget => Said("NextAfterTarget"),
            InterviewStep.AgentPersonality => Said("NextAfterPersonality"),
            InterviewStep.AgentToolkit => Said("NextAfterToolkit"),
            InterviewStep.AgentWorkflows => Said("NextAfterWorkflows"),
            InterviewStep.AgentTerritory => Said("NextAfterTerritory"),
            // The last pass closes onto the agent joining the domain.
            _ => Said("NextAfterAgent")
        };

        // The reply confirms the pass is settled and says what follows.
        return new ToolReply(Said("PassSettled", ("next", nextStep)));
    }

    /// <summary>
    /// Finds a declared tool by exact name, the comparison under which two tools differing only in case collide at startup.
    /// </summary>
    private ToolDraft? Find(string? toolName) =>
        interviewState.Agent.Tools.FirstOrDefault(t =>
            string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));

    /// <summary>
    /// Says what is wrong with a name that has to survive into C# or returns nothing when it is fine.
    /// </summary>
    /// <remarks>Internal because <see cref="CoherenceApplyTools"/> holds the identifiers it writes to the same rule.</remarks>
    /// <param name="what">What the identifier names, as the complaint says it.</param>
    /// <param name="pascalCase">Whether the convention is PascalCase rather than camelCase.</param>
    internal static string IdentifierComplaint(IAlembicPromptService alembicPromptService, string name, string what, bool pascalCase)
    {
        // The shape rule is the one validation holds the name to, so the model is told what the client would be told.
        if (!DraftValidationService.IsIdentifierShape(name))
            return alembicPromptService.ResolveToolInjection("IdentifierShape", ("name", name), ("what", what));

        // A keyword has the shape of an identifier yet cannot stand bare in the generated C#.
        if (DraftValidationService.IsKeyword(name))
            return alembicPromptService.ResolveToolInjection("IdentifierKeyword", ("name", name), ("what", what));

        // The casing is a convention and not a compiler rule, stated so the generated code reads like the framework's own.
        bool casingOk = pascalCase ? char.IsAsciiLetterUpper(name[0]) : char.IsAsciiLetterLower(name[0]);

        // A conforming name draws no complaint.
        return casingOk
            ? string.Empty
            : alembicPromptService.ResolveToolInjection("IdentifierCasing", ("what", what), ("casing", pascalCase ? "PascalCase" : "camelCase"));
    }

    /// <summary>
    /// Reports whether a section landed inside the size its doctrine gives it.
    /// </summary>
    /// <remarks>The size informs and never blocks: a section off its size is recorded and the model is told how to tighten or fill it out.</remarks>
    private string Shaped(string section, string? value, int minimum, int maximum)
    {
        // The section's length as the doctrine measures it.
        int sentences = CountSentences(value);

        // A section inside its shape needs nothing said beyond the fact that it is recorded.
        if (sentences >= minimum && sentences <= maximum)
            return Said("SectionRecorded", ("section", section));

        // Outside its shape it is still recorded: the model is told by how much so that it can tighten or fill it out.
        return Said("SectionOffSize",
            ("section", section),
            ("count", sentences.ToString(CultureInfo.InvariantCulture)),
            ("unit", Said(sentences == 1 ? "SentenceSingular" : "SentencePlural")),
            ("minimum", minimum.ToString(CultureInfo.InvariantCulture)),
            ("maximum", maximum.ToString(CultureInfo.InvariantCulture)),
            ("advice", Said(sentences < minimum ? "SectionTooShort" : "SectionTooLong")));
    }

    /// <summary>
    /// Counts the sentences of a section as terminal punctuation followed by a space or the end.
    /// </summary>
    private static int CountSentences(string? value)
    {
        // A section not written yet has no sentence.
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        string text = value.Trim();
        int count = 0;

        // Every character is a candidate for closing a sentence.
        for (int i = 0; i < text.Length; i++)
        {
            // Only terminal punctuation can close a sentence.
            if (text[i] is not ('.' or '!' or '?'))
                continue;

            // A full stop inside a token (a price or an abbreviation) does not close a sentence.
            if (i == text.Length - 1 || char.IsWhiteSpace(text[i + 1]))
                count++;
        }

        // Prose that never reaches terminal punctuation still counts as one sentence.
        return count == 0 ? 1 : count;
    }
}
