using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Models;
using Alembic.Services;
using Morgana.Contracts;
using Morgana.AI;
using Morgana.AI.Attributes;

namespace Alembic.Tools;

/// <summary>
/// The tools Alembic calls while conducting a pass.
/// </summary>
/// <remarks>
/// <para>
/// Every method here answers <b>the model</b> with a <see cref="ToolReply"/>, not the client. That return channel
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

    /// <summary>The authority on <c>alembic.json</c>, which holds every sentence these tools answer with — see the constructor.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>
    /// Binds the toolset to one interview.
    /// </summary>
    /// <param name="interviewState">The interview these tools write into.</param>
    /// <param name="draftStateService">The domain being built or evolved.</param>
    /// <param name="draftValidationService">The deterministic checks.</param>
    /// <param name="recapService">Composes the prompt the authored agent will really read.</param>
    /// <param name="alembicPromptService">Resolves the sentences that the tools answer the model with.</param>
    public InterviewTools(
        InterviewState interviewState,
        IDraftStateService draftStateService,
        IDraftValidationService draftValidationService,
        IRecapService recapService,
        IAlembicPromptService alembicPromptService)
    {
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
    [Description("Puts one process the client is handing to Morgana on the domain map, or revises one already there. It writes the whole intent — what it is called, what routes to it and the button a user presses to start it — because all three are read against the other entries and not on their own. One entry becomes one intent and one agent. Call it as soon as an entry is clear and again to add the button or sharpen the description.")]
    [RequiresApproval(false)]
    public ToolReply DeclareIntent(
        [Description("A single bare lowercase word, no spaces or punctuation (e.g. 'billing', 'appointments'). It becomes a C# attribute argument and a prompt ID. 'other' is reserved for the classifier's fallback and will be refused.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("The kinds of request that belong here — 'requests to check an order's status, place a new order, or cancel one' — never what an agent does about them. The classifier reads this and nothing else, weighed against every other entry's description.")] [ToolParameter(Records.ToolScope.Request)] string description,
        [Description("A leading emoji then one to three words (e.g. '📄 Billing'). It is read on a button beside every other intent's button, so it names what is got, not what the system does. Omit it on a first pass and add it when the set is written together.")] [ToolParameter(Records.ToolScope.Request)] string? label = null,
        [Description("The sentence that button sends. First person, phrased the way it would actually be typed (e.g. 'I need to book an appointment for my dog'). Omit it on a first pass and add it with the label.")] [ToolParameter(Records.ToolScope.Request)] string? defaultValue = null)
    {
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("IntentNameMissing"));

        if (string.Equals(cleanName, ReservedFallbackIntent, StringComparison.OrdinalIgnoreCase))
            return ToolReply.Refused(Said("IntentReserved", ("name", ReservedFallbackIntent)));

        if (draftStateService.Current?.Intents.Any(i =>
                string.Equals(i.Name, cleanName, StringComparison.OrdinalIgnoreCase)) == true)
            return ToolReply.Refused(Said("IntentTaken", ("name", cleanName)));

        IntentDraft? match = interviewState.Map.FirstOrDefault(i =>
            string.Equals(i.Name, cleanName, StringComparison.OrdinalIgnoreCase));

        bool revision = match is not null;
        IntentDraft intent = match ?? new IntentDraft { Origin = Provenance.Authored };

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
            complaints.Add(Said("IntentNameNotBare"));

        if (string.IsNullOrWhiteSpace(description))
            complaints.Add(Said("IntentDescriptionMissing"));

        if (string.IsNullOrWhiteSpace(intent.Label) || string.IsNullOrWhiteSpace(intent.DefaultValue))
            complaints.Add(Said("IntentButtonMissing"));

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
        int removals = interviewState.Map.RemoveAll(i =>
            string.Equals(i.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        return removals > 0
            ? new ToolReply(Said("IntentDropped", ("name", name ?? string.Empty)))
            : ToolReply.Refused(Said("IntentNotOnMap", ("name", name ?? string.Empty)));
    }

    /// <summary>
    /// Returns the domain map as it currently stands.
    /// </summary>
    /// <remarks>
    /// Read back whole for the same reason the toolkit is: two descriptions overlap or they do not;
    /// that is only visible side by side. It is the one defect no prose downstream repairs — the
    /// user meets it as the wrong agent answering.
    /// </remarks>
    [Description("Returns the map as it stands, in order. Read it before settling: whether two descriptions could claim the same message is only visible with both in front of you.")]
    [RequiresApproval(false)]
    public ToolReply GetDomainMap()
    {
        if (interviewState.Map.Count == 0)
            return new ToolReply(Said("MapEmpty"));

        return new ToolReply(Said("MapHeader") + "\n"
               + string.Join("\n", interviewState.Map.Select((i, n) =>
                   $"{n + 1}. {i.Name}: {i.Description ?? "(nothing said about what routes here)"}"
                   + $"\n    button: {i.Label ?? "(none)"} → \"{i.DefaultValue ?? "(nothing)"}\"")));
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
    [Description("Records the agent's TARGET section: what it does well and existentially, then what it is significant to say it does NOT do. Only edges that will actually be walked into — a limit nobody would test is noise. This section opens the composed prompt, so a capability claimed here and not backed by a tool is the most expensive mistake available to you.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentTarget(
        [Description("Two to four sentences, addressed to the agent as 'you'. Purpose first, then edges. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string target)
    {
        interviewState.Agent.Target = target?.Trim();
        return new ToolReply(Shaped("Target", target, 2, 4));
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
    [Description("Records what a colleague reads before consulting this agent: the scope of its agent, addressed to another agent instead of to itself. A territory, in one or two sentences — what falls here — never a list of what the agent can do, which a caller reads as grounds to rule its question out. Never asked of the client and never shown to them: you write it from the TARGET you have just settled.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentTerritory(
        [Description("The statement, without its section marker.")] [ToolParameter(Records.ToolScope.Request)] string territory)
    {
        interviewState.Agent.Territory = territory?.Trim();
        return new ToolReply(Shaped("Territory", territory, 1, 3));
    }

    /// <summary>
    /// Records the agent's Personality section.
    /// </summary>
    /// <remarks>
    /// Same overwrite-and-report contract as <see cref="SetAgentTarget"/>. Only the <c>AgentPersonality</c> pass declares this tool,
    /// so it is the one place in the interview a voice can be written.
    /// </remarks>
    [Description("Records the agent's PERSONALITY section: the empathy, language, tone and humanity it meets a conversation with. Voice only — a sentence telling the agent what to DO belongs in its instructions. Name which facet of Morgana this agent is; do not list adjectives.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentPersonality(
        [Description("Two to three sentences, addressed to the agent as 'you'. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string personality)
    {
        interviewState.Agent.Personality = personality?.Trim();
        return new ToolReply(Shaped("Personality", personality, 2, 3));
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
    [Description("Records the agent's INSTRUCTIONS section: how it does what it does, what it is trying to achieve on the way and what it is significant to say it must NOT do. The order of the work and its refusals. Only what is true of this domain.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentInstructions(
        [Description("Four to twelve sentences, addressed to the agent as 'you', in the order the work happens. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string instructions)
    {
        interviewState.Agent.Instructions = instructions?.Trim();
        return new ToolReply(Shaped("Instructions", instructions, 2, 5));
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
    [Description("Records the agent's FORMATTING section: how it presents what its own tools return. The one thing no global policy can know for it — Morgana cannot know that an invoice leads with its due date, or that this agent's own confirm-or-cancel deserves two buttons. Say nothing about markdown or length, those are hers; if a button or a card belongs here, name it exactly.")]
    [RequiresApproval(false)]
    public ToolReply SetAgentFormatting(
        [Description("Addressed to the agent as 'you', about this agent's own data: normally two to five sentences, longer only where naming an exact card shape per tool or an exact button payload takes the room it takes. No section label; that is added for you.")] [ToolParameter(Records.ToolScope.Request)] string formatting)
    {
        interviewState.Agent.Formatting = formatting?.Trim();
        return new ToolReply(Shaped("Formatting", formatting, 2, 10));
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
    [Description("Opens a tool, or revises one already open — revising keeps its parameters. Declare it as soon as the action is described, before you know its inputs.")]
    [RequiresApproval(false)]
    public ToolReply DeclareTool(
        [Description("PascalCase, no spaces or punctuation, a verb the agent performs (e.g. 'GetInvoices', 'CancelBooking'). It becomes a C# method name verbatim.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("What the tool does, what it returns and WHEN to call it. Two to four sentences. Say what it does not cover where a neighbouring tool covers it. A description that names only the action leaves the timing to guesswork.")] [ToolParameter(Records.ToolScope.Request)] string description,
        [Description("True when the tool changes something real — it places, books, bills, sends, cancels or deletes — so Morgana runs it only once the user has approved that very call. False when it only looks something up. Inferred from what the tool does, never asked.")] [ToolParameter(Records.ToolScope.Request)] bool requiresApproval)
    {
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("ToolNameMissing"));

        ToolDraft? match = Find(cleanName);
        bool revision = match is not null;

        ToolDraft tool = match ?? new ToolDraft { Name = cleanName, Origin = Provenance.Authored };
        tool.Description = description?.Trim();
        tool.RequiresExecutionApproval = requiresApproval;

        if (!revision)
            interviewState.Agent.Tools.Add(tool);

        // Reported, never rewritten: the name is domain vocabulary and a silent correction leaves
        // Alembic telling the client one word while the configuration carries another.
        string complaint = IdentifierComplaint(alembicPromptService, cleanName, "tool name", pascalCase: true);

        return new ToolReply((revision ? Said("ToolRevised", ("name", cleanName)) : Said("ToolDeclared", ("name", cleanName)))
               + (complaint.Length > 0 ? " " + complaint : string.Empty)
               + (string.IsNullOrWhiteSpace(description) ? " " + Said("ToolDescriptionMissing") : string.Empty));
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
        if (Find(toolName) is not { } tool)
            return ToolReply.Refused(Said("ParameterToolMissing", ("tool", toolName ?? string.Empty)));

        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("ParameterNameMissing"));

        string? resolvedScope = ResolveScope(scope);

        ToolParameterDraft? match = tool.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, cleanName, StringComparison.Ordinal));

        bool revision = match is not null;
        ToolParameterDraft parameter = match ?? new ToolParameterDraft { Name = cleanName };

        parameter.Description = description?.Trim();
        parameter.Scope = resolvedScope;
        parameter.Required = required;
        parameter.Shared = shared;

        if (!revision)
            tool.Parameters.Add(parameter);

        List<string> complaints = [];

        string identifier = IdentifierComplaint(alembicPromptService, cleanName, "parameter name", pascalCase: false);
        if (identifier.Length > 0)
            complaints.Add(identifier);

        complaints.AddRange(ScopeComplaints(alembicPromptService, resolvedScope, required, shared));

        // The order is the signature, so an optional parameter followed by a required one is not a
        // preference: C# could not declare the signature.
        if (Enumerable.Range(0, tool.Parameters.Count).Any(index => DraftValidationService.RequiredFollowsOptional(tool.Parameters, index)))
            complaints.Add(Said("ParameterRequiredAfterOptional"));

        return new ToolReply(Said("ParameterRecorded", ("name", cleanName), ("tool", tool.Name ?? string.Empty))
               + (complaints.Count > 0 ? " " + string.Join(" ", complaints) : string.Empty));
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
    /// <param name="alembicPromptService">Resolves the sentences of the complaints.</param>
    /// <param name="scope">The scope as <see cref="ResolveScope"/> read it.</param>
    /// <param name="required">Whether the call fails without the parameter.</param>
    /// <param name="shared">Whether the resolved value is published for other agents.</param>
    internal static List<string> ScopeComplaints(IAlembicPromptService alembicPromptService, string? scope, bool required, bool shared)
    {
        List<string> complaints = [];

        if (!DraftValidationService.IsScope(scope))
            complaints.Add(alembicPromptService.ResolveToolInjection("ScopeUnknown",
                ("scope", scope ?? string.Empty), ("context", ContextScope), ("request", RequestScope)));

        if (DraftValidationService.IsSharedRequest(scope, shared))
            complaints.Add(alembicPromptService.ResolveToolInjection("ScopeRequestShared"));

        if (DraftValidationService.IsOptionalContext(scope, required))
            complaints.Add(alembicPromptService.ResolveToolInjection("ScopeContextOptional"));

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
        if (Find(toolName) is not { } tool)
            return ToolReply.Refused(Said("DropToolUnknown", ("tool", toolName ?? string.Empty)));

        int removals = tool.Parameters.RemoveAll(p =>
            string.Equals(p.Name, parameterName?.Trim(), StringComparison.Ordinal));

        return removals > 0
            ? new ToolReply(Said("ParameterDropped", ("name", parameterName ?? string.Empty), ("tool", tool.Name ?? string.Empty)))
            : ToolReply.Refused(Said("ParameterAbsent", ("tool", tool.Name ?? string.Empty), ("name", parameterName ?? string.Empty)));
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
    [Description("Adds a field to what a declared tool hands back, or revises one already there by name. The agent's model reads exactly these fields in the tool's result and the client's code fills exactly these. The field named 'error' is the failure: it holds why the call failed and stays empty whenever the call succeeds.")]
    [RequiresApproval(false)]
    public ToolReply SetToolReturn(
        [Description("The exact name of a tool you have already declared.")] [ToolParameter(Records.ToolScope.Request)] string toolName,
        [Description("camelCase, no spaces or punctuation (e.g. 'orderId'). It is the name the agent's model reads in the result.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("What the field holds, with an example where one helps.")] [ToolParameter(Records.ToolScope.Request)] string description,
        [Description("The C# type of the field exactly as the record writes it, preferring a simple one: string, int, long, decimal, bool, DateOnly or List<string>. The field named 'error' is always string.")] [ToolParameter(Records.ToolScope.Request)] string type)
    {
        if (Find(toolName) is not { } tool)
            return ToolReply.Refused(Said("ReturnToolMissing", ("tool", toolName ?? string.Empty)));

        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("ReturnNameMissing"));

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

        if (cleanType.Length == 0)
            complaints.Add(Said("ReturnTypeMissing"));
        else if (!DraftValidationService.ParsesAsType(cleanType))
            complaints.Add(Said("ReturnTypeInvalid", ("type", cleanType)));

        if (DraftValidationService.IsNonTextFailureField(cleanName, cleanType))
            complaints.Add(Said("ReturnFailureNotText", ("name", cleanName), ("tool", tool.Name ?? string.Empty)));

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
        if (Find(toolName) is not { } tool)
            return ToolReply.Refused(Said("DropToolUnknown", ("tool", toolName ?? string.Empty)));

        int removals = tool.Returns.RemoveAll(r =>
            string.Equals(r.Name, name?.Trim(), StringComparison.Ordinal));

        return removals > 0
            ? new ToolReply(Said("ReturnDropped", ("name", name ?? string.Empty), ("tool", tool.Name ?? string.Empty)))
            : ToolReply.Refused(Said("ReturnAbsent", ("tool", tool.Name ?? string.Empty), ("name", name ?? string.Empty)));
    }

    /// <summary>
    /// Removes a tool and everything on it.
    /// </summary>
    /// <remarks>
    /// The workflow steps that still name it are left as they are and reported, so the pass that
    /// owns workflows repairs them rather than a tool being dropped behind their back.
    /// </remarks>
    [Description("Removes a tool and every parameter on it. Use it when the agent turns out to have no such job, or when you have split one action into two tools that cannot be told apart.")]
    [RequiresApproval(false)]
    public ToolReply DropTool(
        [Description("The exact name of the tool to remove.")] [ToolParameter(Records.ToolScope.Request)] string toolName)
    {
        string cleanName = toolName?.Trim() ?? string.Empty;

        int removals = interviewState.Agent.Tools.RemoveAll(t =>
            string.Equals(t.Name, cleanName, StringComparison.Ordinal));

        if (removals == 0)
            return ToolReply.Refused(Said("DropToolUnknown", ("tool", toolName ?? string.Empty)));

        List<string> orphans =
        [.. interviewState.Agent.Workflows.SelectMany(workflow => workflow.Steps
                .Where(step => step.Tools.Contains(cleanName, StringComparer.Ordinal))
                .Select(step => Said("ToolStillInStep",
                    ("tool", cleanName), ("step", step.Name), ("workflow", workflow.Name ?? string.Empty))))];

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
        if (interviewState.Agent.Tools.Count == 0)
            return new ToolReply(Said("ToolkitEmpty") + "\n\n"
                   + DescribeWorkflows(alembicPromptService, interviewState.Agent.Workflows));

        IEnumerable<string> lines = interviewState.Agent.Tools.Select(t =>
            $"- {t.Name}{(t.RequiresExecutionApproval ? " " + Said("ToolWaitsForApproval") : string.Empty)}: {t.Description ?? "(no description)"}"
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

        return new ToolReply(Said("ToolkitHeader") + "\n" + string.Join("\n", lines)
               + "\n\n" + DescribeWorkflows(alembicPromptService, interviewState.Agent.Workflows));
    }

    /// <summary>
    /// Opens a workflow, or revises the description of one already open.
    /// </summary>
    /// <remarks>
    /// Revising rewrites the description and keeps the steps: a workflow is settled in several turns
    /// (what it achieves comes out of one answer, its steps out of the next), so re-declaring it to
    /// sharpen the description must not silently empty it.
    /// </remarks>
    [Description("Opens a workflow, or revises the description of one already open — revising keeps its steps.")]
    [RequiresApproval(false)]
    public ToolReply DeclareWorkflow(
        [Description("PascalCase, no spaces or punctuation (e.g. 'PlaceOrder'). The agent's model starts the workflow by this name.")] [ToolParameter(Records.ToolScope.Request)] string name,
        [Description("What the procedure achieves for the person, in one sentence. The agent's model reads it to decide when to start this workflow.")] [ToolParameter(Records.ToolScope.Request)] string description)
    {
        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("WorkflowNameMissing"));

        // The name is what the launcher function is named after, so a workflow kept under one that has
        // to change would survive the corrected call as a second workflow.
        string complaint = IdentifierComplaint(alembicPromptService, cleanName, "workflow name", pascalCase: true);

        if (complaint.Length > 0)
            return ToolReply.Refused(Said("WorkflowNotRecorded") + " " + complaint);

        WorkflowDraft? match = FindWorkflow(cleanName);
        WorkflowDraft workflow = match ?? new WorkflowDraft { Name = cleanName, Origin = Provenance.Authored };
        workflow.Description = description?.Trim();

        if (match is null)
            interviewState.Agent.Workflows.Add(workflow);

        return new ToolReply((match is not null ? Said("WorkflowRevised", ("name", cleanName)) : Said("WorkflowDeclared", ("name", cleanName)))
               + (string.IsNullOrWhiteSpace(description) ? " " + Said("WorkflowDescriptionMissing") : string.Empty));
    }

    /// <summary>
    /// Adds a step at the end of a workflow, or revises the tools of one already there by name and in place.
    /// </summary>
    /// <remarks>
    /// Revision keeps the step's place, because the order of the steps is the order the procedure
    /// was described in and the first one is where it starts. The edges of the step stay as they are:
    /// <see cref="GetFindings"/> reports one whose tool the step no longer offers.
    /// </remarks>
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
        if (FindWorkflow(workflow) is not { } owner)
            return ToolReply.Refused(Said("StepWorkflowMissing", ("workflow", workflow ?? string.Empty)));

        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused(Said("StepNameMissing"));

        List<string> stepTools = [.. (tools ?? []).Select(tool => tool?.Trim() ?? string.Empty)
                                                  .Where(tool => tool.Length > 0)
                                                  .Distinct(StringComparer.Ordinal)];
        List<string> complaints = [];

        string identifierComplaint = IdentifierComplaint(alembicPromptService, cleanName, "step name", pascalCase: true);

        if (identifierComplaint.Length > 0)
            complaints.Add(identifierComplaint);

        // An agent that acquires tools from an MCP server names tools nothing here can see, so only
        // an agent with none of those can have a tool judged absent on the spot.
        if (interviewState.Agent.Code.MCPServers.Count == 0)
            foreach (string tool in stepTools.Where(tool => Find(tool) is null))
                complaints.Add(Said("StepToolUnknown", ("tool", tool)));

        if (stepTools.Count == 0)
            complaints.Add(Said("StepNeedsTool"));

        if (complaints.Count > 0)
            return ToolReply.Refused(Said("StepNotRecorded") + " " + string.Join(" ", complaints) + " " + Said("StepCorrect"));

        Records.WorkflowStep stepRecord = new(cleanName, stepTools);
        int position = owner.Steps.FindIndex(step => string.Equals(step.Name, cleanName, StringComparison.Ordinal));

        if (position >= 0)
            owner.Steps[position] = stepRecord;
        else
            owner.Steps.Add(stepRecord);

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
        if (FindWorkflow(workflow) is not { } owner)
            return ToolReply.Refused(Said("DropWorkflowUnknown", ("workflow", workflow ?? string.Empty)));

        int removals = owner.Edges.RemoveAll(edge => IsEdge(edge, source, tool, failure));
        string edgeKind = Said(failure ? "EdgeKindFailure" : "EdgeKindSuccess");

        return removals > 0
            ? new ToolReply(Said("EdgeDropped",
                ("edge", edgeKind), ("tool", tool ?? string.Empty), ("source", source ?? string.Empty), ("workflow", owner.Name ?? string.Empty)))
            : ToolReply.Refused(Said("EdgeAbsent",
                ("workflow", owner.Name ?? string.Empty), ("edge", edgeKind), ("tool", tool ?? string.Empty), ("source", source ?? string.Empty)));
    }

    /// <summary>
    /// Records an edge under the key (source, tool, outcome), replacing the one already held there in place.
    /// </summary>
    /// <remarks>
    /// Only what makes the edge unrecordable is refused (a workflow or step that is not there, a tool the
    /// source step does not offer): whether the carried names are returned by the tool is for <see cref="GetFindings"/>.
    /// </remarks>
    private ToolReply RecordEdge(string workflow, string source, string target, string tool, string[]? carrying, bool onFailure)
    {
        if (FindWorkflow(workflow) is not { } owner)
            return ToolReply.Refused(Said("EdgeWorkflowMissing", ("workflow", workflow ?? string.Empty)));

        string cleanSource = (source ?? string.Empty).Trim();
        string cleanTarget = (target ?? string.Empty).Trim();
        string cleanTool = (tool ?? string.Empty).Trim();
        List<string> complaints = [];

        Records.WorkflowStep? sourceStep = owner.Steps.FirstOrDefault(step => string.Equals(step.Name, cleanSource, StringComparison.Ordinal));

        if (sourceStep is null)
            complaints.Add(Said("EdgeNoSuchStep", ("workflow", owner.Name ?? string.Empty), ("step", source ?? string.Empty)));
        else if (!sourceStep.Tools.Contains(cleanTool, StringComparer.Ordinal))
            complaints.Add(Said("EdgeToolNotOffered", ("tool", cleanTool), ("step", cleanSource)));

        if (!owner.Steps.Any(step => string.Equals(step.Name, cleanTarget, StringComparison.Ordinal)))
            complaints.Add(Said("EdgeNoSuchStep", ("workflow", owner.Name ?? string.Empty), ("step", target ?? string.Empty)));

        if (complaints.Count > 0)
            return ToolReply.Refused(Said("EdgeNotRecorded") + " " + string.Join(" ", complaints) + " " + Said("EdgeCorrect"));

        List<string> cargo = [.. (carrying ?? []).Select(name => name?.Trim() ?? string.Empty)
                                                   .Where(name => name.Length > 0)
                                                   .Distinct(StringComparer.Ordinal)];
        Records.WorkflowEdge edgeRecord = new(cleanSource, cleanTarget, cleanTool, onFailure, cargo);
        int position = owner.Edges.FindIndex(edge => IsEdge(edge, cleanSource, cleanTool, onFailure));

        if (position >= 0)
            owner.Edges[position] = edgeRecord;
        else
            owner.Edges.Add(edgeRecord);

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
        if (FindWorkflow(workflow) is not { } owner)
            return ToolReply.Refused(Said("DropWorkflowUnknown", ("workflow", workflow ?? string.Empty)));

        string cleanStep = step?.Trim() ?? string.Empty;
        int removals = owner.Steps.RemoveAll(candidate => string.Equals(candidate.Name, cleanStep, StringComparison.Ordinal));

        if (removals == 0)
            return ToolReply.Refused(Said("StepAbsent", ("workflow", owner.Name ?? string.Empty), ("step", step ?? string.Empty)));

        // An edge whose end is gone would name a step that no longer exists.
        int edgesRemoved = owner.Edges.RemoveAll(edge =>
            string.Equals(edge.Source, cleanStep, StringComparison.Ordinal) || string.Equals(edge.Target, cleanStep, StringComparison.Ordinal));

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
        int removals = interviewState.Agent.Workflows.RemoveAll(workflow =>
            string.Equals(workflow.Name, name?.Trim(), StringComparison.Ordinal));

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
    /// <remarks>
    /// Internal because the coherence passes read an agent's workflows in the same words the
    /// interview does, so the prose they judge is set against the procedure it would restate.
    /// </remarks>
    /// <param name="alembicPromptService">Resolves the sentences that frame the rendering.</param>
    /// <param name="workflows">The workflows to render.</param>
    internal static string DescribeWorkflows(IAlembicPromptService alembicPromptService, IReadOnlyList<WorkflowDraft> workflows)
    {
        if (workflows.Count == 0)
            return alembicPromptService.ResolveToolInjection("WorkflowsNone");

        return alembicPromptService.ResolveToolInjection("WorkflowsHeader") + "\n\n"
               + string.Join("\n\n", workflows.Select(workflow =>
                   $"Workflow {workflow.Name ?? "(unnamed)"}: {workflow.Description ?? "(no description)"}"
                   + (workflow.Steps.Count == 0
                       ? "\n  (no step yet)"
                       : string.Concat(workflow.Steps.Select((step, position) =>
                           $"\n  {position + 1}. {step.Name} — tools: "
                           + (step.Tools.Count == 0 ? "(none yet)" : string.Join(", ", step.Tools)))))
                   + string.Concat(workflow.Edges.Select(edge => "\n  " + DescribeEdge(edge)))));
    }

    /// <summary>
    /// One edge as a line: where the call leads and the values it carries.
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
    /// <remarks>
    /// Each pass is a fresh agent with a fresh session, so nothing of the previous conversation
    /// carries over — deliberately, because a toolkit pass that still has the whole functional
    /// interview in its context spends it re-litigating decisions already taken. What must carry
    /// over is the configuration and the configuration is exactly what this returns.
    /// </remarks>
    [Description("Returns the agent as it currently stands: the intent it answers, what it is for, where it stops, how it sounds and what it can reach. Each step runs with a fresh memory and only the configuration carries over, so this is the whole of what you know about it. On an agent already in the client's domain, all of it was written before today.")]
    [RequiresApproval(false)]
    public ToolReply GetAgentSoFar()
    {
        if (string.IsNullOrWhiteSpace(interviewState.Intent.Name))
            return new ToolReply(Said("AgentNoIntent"));

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
            sections.Add(DescribeWorkflows(alembicPromptService, interviewState.Agent.Workflows));

        return new ToolReply(Said("AgentSoFarHeader") + "\n\n"
               + string.Join("\n\n", sections));
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
    [Description("Offers words for this agent's voice under the question you are about to ask, to be picked from freely — several, one, or none and the text box stays open beside them. Call it BEFORE writing the question. Every word must be a way Morgana herself could speak: this section specialises her voice and never replaces it.")]
    [RequiresApproval(false)]
    public ToolReply SetTraits(
        [Description("JSON array of eight to fourteen single adjectives, in the client's language: [\"precise\",\"unhurried\",\"reassuring\"]. Real alternatives rather than shades of one temper, each plausible for THIS domain and none of them a way Morgana would never speak.")] [ToolParameter(Records.ToolScope.Request)] string traits)
    {
        try
        {
            List<string>? values = JsonSerializer.Deserialize<List<string>>(
                traits, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            List<string> words = [.. (values ?? []).Select(w => w.Trim()).Where(w => w.Length > 0)];

            if (words.Count == 0)
                return ToolReply.Refused(Said("TraitsNone"));

            interviewState.PendingTraits.Clear();
            interviewState.PendingTraits.AddRange(words);

            return new ToolReply(Said("TraitsDrawn", ("count", words.Count.ToString(CultureInfo.InvariantCulture))));
        }
        catch (JsonException ex)
        {
            return ToolReply.Refused(Said("TraitsMalformed", ("reason", ex.Message)));
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
    /// The map stands on the whole domain and holds no agent, so what is learned there is about
    /// the business or it is lost with the empty agent.
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
    [Description("Writes down one thing the client has just told you about how their work actually goes — not what you decided about it. Every step after this one opens holding what is on record for the agent in hand and for the business, so call it the moment an answer says something a later step would otherwise have to ask again: what they sell, who writes in and about what, what they open to answer it, what goes wrong, what they never do. One fact per call, in their own words. Never what you wrote into a section — that is already in the configuration and a second copy of it here is a second thing to keep true. Where what they just said contradicts something on record, say so in 'corrects' and the wrong one goes: a record nobody can correct is worse than no record, because every step after you will hold it and none of them can doubt it.")]
    [RequiresApproval(false)]
    public ToolReply NoteDomainFact(
        [Description("What this is about, in one or two of THEIR words — 'prices', 'deposits', 'custom cakes'. Facts about the same thing take the same subject: it is what another agent's step reads to decide whether to fetch this at all.")] [ToolParameter(Records.ToolScope.Request)] string subject,
        [Description("One short sentence about their work, in their vocabulary, never about the agent: 'the catalogue changes with the season', 'custom cakes are quoted at an appointment, never over the counter'. Standing on one entry of the map it is kept with that agent; drawing the map or closing the domain it is kept for the business as a whole.")] [ToolParameter(Records.ToolScope.Request)] string fact,
        [Description("Any part of the sentence already on record that this puts right, where they have just contradicted it — a reading taken off their upload most of all. Leave it out when nothing is being corrected.")] [ToolParameter(Records.ToolScope.Request)] string? corrects = null)
    {
        string sentence = fact.Trim();
        string topic = subject.Trim();

        if (sentence.Length == 0 || topic.Length == 0)
            return ToolReply.Refused(Said("FactIncomplete"));

        List<KnownFact> facts = Memory;
        int removals = corrects is { Length: > 0 } mistake ? Forget(facts, mistake) : 0;

        // What the client says stands over what Alembic read off their upload about the same
        // subject: a reading of somebody else's prose loses to the person whose shop it is.
        removals += facts.RemoveAll(entry =>
            entry.Inferred && string.Equals(entry.Subject, topic, StringComparison.OrdinalIgnoreCase));

        if (facts.Any(entry => string.Equals(entry.Fact, sentence, StringComparison.OrdinalIgnoreCase)))
            return new ToolReply(Said("FactDuplicate"));

        if (facts.Count >= MemoryCeiling)
            return ToolReply.Refused(Said("FactMemoryFull", ("count", facts.Count.ToString(CultureInfo.InvariantCulture))));

        facts.Add(new KnownFact(topic, sentence, Inferred: false));

        return new ToolReply(Said("FactWritten",
            ("subject", topic),
            ("replaced", removals > 0 ? Said("FactReplaced", ("count", removals.ToString(CultureInfo.InvariantCulture))) : string.Empty),
            ("count", facts.Count.ToString(CultureInfo.InvariantCulture))));
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
    [Description("Takes something off the record that is not true of their work. Use it the moment the client says otherwise. Use it on your own readings of their upload without hesitation — nobody confirmed those. Left standing, a wrong fact is held by every step after this one and none of them has any way to doubt it.")]
    [RequiresApproval(false)]
    public ToolReply DropDomainFact(
        [Description("Any part of the sentence to remove, or its subject, enough to tell it from the others on record.")] [ToolParameter(Records.ToolScope.Request)] string fact)
    {
        string mistake = fact.Trim();

        if (mistake.Length == 0)
            return ToolReply.Refused(Said("FactDropEmpty"));

        int removals = Forget(Memory, mistake);

        return removals == 0
            ? ToolReply.Refused(Said("FactAbsent"))
            : new ToolReply(Said("FactsDropped", ("count", removals.ToString(CultureInfo.InvariantCulture))));
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
    [Description("Hands back what is on record about one of the OTHER agents of this domain. You open holding what is known about the agent in hand and about the business; for every other agent you hold only the subjects it keeps — a domain read whole would be forty sentences carried into every question, nearly all about counters this step will never touch. Call this when a subject listed against another agent bears on the question you are about to ask: whether the counter next door already takes deposits decides whether this one should.")]
    [RequiresApproval(false)]
    public ToolReply RecallAgent(
        [Description("The agent's own intent name, exactly as the opening message lists it.")] [ToolParameter(Records.ToolScope.Request)] string intent)
    {
        string intentName = intent.Trim();
        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        // An agent of this domain is an entry of the map, written or still ahead — the same universe
        // the opening message lists its neighbours from. Resolved against the written agents alone,
        // this denied the existence of every agent the interview had not reached yet, which is most
        // of them on the first agent and all of them the client had just dictated.
        IntentDraft? entry = draft.Intents.Concat(interviewState.Map).FirstOrDefault(candidate =>
            string.Equals(candidate.Name, intentName, StringComparison.OrdinalIgnoreCase));

        AgentDraft? agent = draft.Agents.FirstOrDefault(agent =>
            string.Equals(agent.ID, intentName, StringComparison.OrdinalIgnoreCase));

        if (entry is null && agent is null)
            return ToolReply.Refused(Said("RecallUnknown", ("name", intentName)));

        // What the map says about an agent nobody has opened yet is the whole of what is known about
        // it. It is worth more than a refusal: the routing sentence the client dictated is the
        // only account of that counter anybody has.
        if (agent is null || agent.Known.Count == 0)
            return new ToolReply(string.IsNullOrWhiteSpace(entry?.Description)
                ? Said("RecallNothing", ("name", intentName))
                : Said("RecallMapOnly", ("name", intentName), ("description", entry.Description)));

        return new ToolReply(Said("RecallHeader", ("name", intentName)) + "\n"
               + string.Join("\n", agent.Known.Select(entry =>
                   $"- {entry.Subject}: {entry.Fact}" + (entry.Inferred ? " " + Said("RecalledInferred") : string.Empty))));
    }

    /// <summary>
    /// Removes whatever on record carries the wrong sentence, by subject or by wording.
    /// </summary>
    private static int Forget(List<KnownFact> facts, string mistake) =>
        facts.RemoveAll(entry =>
            entry.Fact.Contains(mistake, StringComparison.OrdinalIgnoreCase)
            || mistake.Contains(entry.Fact, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Subject, mistake, StringComparison.OrdinalIgnoreCase));

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
    [Description("States, over the question you are about to ask, what this step adds to the agent in hand. Call it in the turn a step lands on, before the question, every time without exception: a step that lands with nothing but a question leaves the client working out what this screen is for from the question alone. Call it in no other turn — a confirmation or a follow-up is not a landing and what stands there would say the interview had moved on when it has not. It is not room for anything else: never an answer to what they just said, never an apology, never a correction of your own last turn. Those belong in the question.")]
    [RequiresApproval(false)]
    public ToolReply SetStepPlacing(
        [Description("One short sentence in the second person, naming what THEY will be able to do once this step is settled, in the things their work is actually made of — the orders, the counter, the people who write in. Name neither the agent nor the step: both already stand lit on the screen. Where the sentence needs a subject it is the part of their work this agent stands for, in their own words. Two things it is never: a line about what software becomes able to do ('this is where the catalogue stops being just words and becomes something you can really consult' is a brochure and tells a baker nothing) or a restatement of why this stage exists, which is written above for you and not for them. The test is simple: if the same sentence could open this step for a garage, a vet and a bank alike, it places nothing. Never place it at the expense of the steps behind it: what those settled is already their configuration, so a step announcing that things now finally become real tells them the screens they have just filled in were a game.")] [ToolParameter(Records.ToolScope.Request)] string placing)
    {
        string placingText = placing.Trim();

        if (placingText.Length == 0)
            return ToolReply.Refused(Said("PlacingEmpty"));

        interviewState.PendingPlacing = placingText;

        return new ToolReply(Said("PlacingSet"));
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
    [Description("Shows the client, word for word, the prose you have just written for their agent. Call it on the turn that asks whether a section is right, before the question: what they are approving is those exact words, so those words stand on their own and your own sentence introduces them rather than containing them. 'I have written a fond and careful voice that follows…' is one stretch of prose in which nothing marks where you stop speaking and their agent's text begins; an approval given to that approves nothing in particular. Only what is actually written — never a paraphrase, never a summary of it.")]
    [RequiresApproval(false)]
    public ToolReply ShowWhatIsWritten(
        [Description("The section's prose exactly as it now stands, with nothing added around it and no quotation marks of your own.")] [ToolParameter(Records.ToolScope.Request)] string written)
    {
        string prose = written.Trim();

        if (prose.Length == 0)
            return ToolReply.Refused(Said("QuotedEmpty"));

        interviewState.PendingQuoted = prose;

        return new ToolReply(Said("QuotedSet"));
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
    [Description("Puts a worked example in the answer box under the question you are about to ask, greyed out, for them to take and cut about or to type over.")]
    [RequiresApproval(false)]
    public ToolReply SetExample(
        [Description("An answer somebody in THEIR trade might have given to the question you are about to ask, cut to the size of that question — forty to sixty words where it opens a step, ten to twenty where it follows one up — in their vocabulary and about their own work — never about another business, never a description of what to write and never the question itself put into their mouth: an example that says what the question says adds nothing to the screen it stands on. It teaches the register, the length and the level of detail at once. Send it unquoted: it stands in the box as text they can send as it is, so quotation marks around it arrive as part of their own answer. Make it close enough to their work to be worth editing and specific enough that it cannot be agreed with as it stands: an example that fits them exactly is one they will press past without adding what only they know.")] [ToolParameter(Records.ToolScope.Request)] string example)
    {
        // The box holds it as the client's own text, so a pair of quotation marks around it is text
        // they would be sending. One at a single end is what a half-escaped payload leaves behind.
        string exampleText = example.Trim().Trim('"').Trim();

        if (exampleText.Length == 0)
            return ToolReply.Refused(Said("ExampleEmpty"));

        interviewState.PendingExample = exampleText;

        return new ToolReply(Said("ExampleSet"));
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
    [Description("Attaches to the question you are about to ask the one button that answers it without adding anything — agreement, or that there is nothing to add — so a client in that position settles the turn with one press instead of a typed sentence. Call it BEFORE writing the question: the button renders below it. The text box stays open either way.")]
    [RequiresApproval(false)]
    public ToolReply SetChoice(
        [Description("What the client reads on the button. It carries the answer that adds nothing and has to answer the question AS YOU PUT IT — 'That's everything' where you asked whether that is everything, 'Nothing missing' where you asked whether anything is missing, 'No, nothing like that' where you asked whether there is something the agent should never do.")] [ToolParameter(Records.ToolScope.Request)] string label,
        [Description("The complete answer the button sends, in the client's register, reading as something they would plausibly have typed.")] [ToolParameter(Records.ToolScope.Request)] string value)
    {
        if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value))
            return ToolReply.Refused(Said("ChoiceIncomplete"));

        interviewState.PendingChoice = new QuickReply("agree", label.Trim(), value.Trim());

        return new ToolReply(Said("ChoiceSet"));
    }

    /// <summary>
    /// Returns the intents already in the domain.
    /// </summary>
    /// <remarks>
    /// Two sources and both are load-bearing. What the domain already holds arrived from earlier
    /// sittings or an uploaded configuration; what the map still holds is what this interview has
    /// promised to write next. A description only has to be told apart from both.
    /// </remarks>
    [Description("Returns the name and description of every intent already in the domain, the descriptions a new one is weighed against.")]
    [RequiresApproval(false)]
    public ToolReply GetExistingIntents()
    {
        IEnumerable<string> domainLines = (draftStateService.Current?.Intents ?? [])
            .Where(i => !string.Equals(i.Name, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase))
            .Select(i => $"- {i.Name}: {i.Description} {Said("IntentAlreadyInDomain")}");

        IEnumerable<string> mapLines = interviewState.Map
            .Where(i => !ReferenceEquals(i, interviewState.Intent))
            .Select(i => $"- {i.Name}: {i.Description} {Said("IntentOnMapNotWritten")}");

        List<string> lines = [.. domainLines, .. mapLines];

        return new ToolReply(lines.Count == 0
            ? Said("IntentsNone")
            : Said("IntentsHeader") + "\n" + string.Join("\n", lines));
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
    [Description("Returns the complete prompt this agent's model will read, composed as Morgana composes it — her layer, the fences and your sections beneath. Reading your own work composed is the only way to see whether your layer contradicts hers or merely repeats her.")]
    [RequiresApproval(false)]
    public async Task<ToolReply> GetComposedPrompt()
    {
        if (string.IsNullOrWhiteSpace(interviewState.Agent.Target))
            return new ToolReply(Said("ComposeNoTarget"));

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

        return new ToolReply(domain < 0
            ? Said("ComposedWhole") + "\n\n" + recap.SystemPrompt
            : Said("ComposedYours") + "\n\n" + recap.SystemPrompt[domain..]);
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
    [Description("Returns the card this agent will present to anyone who might consult it: what it answers for and the skills it advertises. Call it once the territory is written — a colleague weighing a question reads that description and nothing else, so this is where a sentence that reads as a list of functions is visible.")]
    [RequiresApproval(false)]
    public ToolReply GetAgentCard()
    {
        return new ToolReply(Said("AgentCard",
            ("intent", interviewState.Intent.Name ?? string.Empty),
            ("description", CardProjection.Description(interviewState.Intent, interviewState.Agent)),
            ("skills", interviewState.Agent.Tools.Count == 0
                ? Said("AgentCardNoSkills")
                : CardProjection.Skills(interviewState.Agent))));
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
    [Description("Rewrites what this entry routes on, in the sharper words the territory settled. The classifier reads it against every other entry's description, so read those back first and leave this one distinct from all of them. Only this entry: no other intent of the domain is yours.")]
    [RequiresApproval(false)]
    public ToolReply SetIntentDescription(
        [Description("What lands on this agent, said for the classifier: the subject and the kinds of request that belong to it, in the client's own words. Never the agent's boundaries, its tools or its voice.")] [ToolParameter(Records.ToolScope.Request)] string description)
    {
        string descriptionText = (description ?? string.Empty).Trim();

        if (descriptionText.Length == 0)
            return ToolReply.Refused(Said("IntentDescriptionEmpty"));

        interviewState.Intent.Description = descriptionText;

        return new ToolReply(Said("IntentRoutesOn", ("name", interviewState.Intent.Name ?? string.Empty), ("description", descriptionText)));
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
    [Description("Returns everything wrong with this agent that can be decided without a model. Fix what it reports rather than explaining it away.")]
    [RequiresApproval(false)]
    public ToolReply GetFindings()
    {
        if (string.IsNullOrWhiteSpace(interviewState.Intent.Name))
            return new ToolReply(Said("FindingsNoIntent"));

        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        DomainDraft probe = new DomainDraft
        {
            Intents = [.. draft.Intents, interviewState.Intent],
            Agents = [.. draft.Agents, interviewState.Agent]
        };

        string intentName = interviewState.Intent.Name!;

        List<ValidationFinding> findings =
            [.. draftValidationService.Validate(probe)
                .Where(f => f.Where.Contains($"'{intentName}'", StringComparison.OrdinalIgnoreCase)
                            || f.Where.StartsWith($"{intentName}.", StringComparison.OrdinalIgnoreCase)
                            || f.Where == "domain")];

        return new ToolReply(findings.Count == 0
            ? Said("FindingsNone")
            : string.Join("\n", findings.Select(f => $"[{f.Severity}] {f.Where}: {f.Message} — {f.Because}")));
    }

    /// <summary>
    /// Declares the pass settled.
    /// </summary>
    /// <remarks>
    /// Believed only as far as the state machine can confirm it. Which fields are set is a fact
    /// and facts are not a model's to assert.
    /// </remarks>
    [Description("Declares this pass settled. The declaration lives in this call and nowhere else: never write a marker, a token or a closing formula into your text to mean it.")]
    [RequiresApproval(false)]
    public ToolReply SetPassCompleted()
    {
        // Correcting, every section is written already, so each pass could settle the moment it
        // opened and the client would be walked through an edit that asked them nothing at all —
        // which is how a client who came to change a voice reached the end with the voice they
        // came to change. A pass may still settle on their first word; the doctrine's own fast
        // path stands: what it may not do is settle before they have said one.
        if (interviewState.Revision is not null && interviewState.Exchanges == interviewState.PassOpenedAt)
            return ToolReply.Refused(Said("PassNotYetSaid"));

        IReadOnlyList<string> gaps = interviewState.Missing();

        if (gaps.Count > 0)
            return ToolReply.Refused(Said("PassIncomplete",
                ("missing", string.Join(", ", gaps)),
                ("advice", Said(gaps.Count == 1 ? "PassMissingOne" : "PassMissingMany"))));

        interviewState.ReadyForReview = true;

        // What comes next is told by the pass that has just closed, so the model announces it without waiting for the state machine to open it.
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

        return new ToolReply(Said("PassSettled", ("next", nextStep)));
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
    /// <param name="alembicPromptService">Resolves the sentences of the complaints.</param>
    /// <param name="name">The identifier the model wrote.</param>
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

        bool casingOk = pascalCase ? char.IsAsciiLetterUpper(name[0]) : char.IsAsciiLetterLower(name[0]);

        return casingOk
            ? string.Empty
            : alembicPromptService.ResolveToolInjection("IdentifierCasing", ("what", what), ("casing", pascalCase ? "PascalCase" : "camelCase"));
    }



    /// <summary>
    /// Reports whether a section landed inside the size its doctrine gives it.
    /// </summary>
    /// <remarks>
    /// Recorded either way. The size is a shape, not a gate: a Target of five sentences is still
    /// better than no Target and Alembic is told so it can tighten rather than blocked so it must.
    /// </remarks>
    private string Shaped(string section, string? value, int minimum, int maximum)
    {
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
    /// Counts sentences crudely — terminal punctuation followed by a space or the end.
    /// </summary>
    private static int CountSentences(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        string text = value.Trim();
        int count = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?'))
                continue;

            if (i == text.Length - 1 || char.IsWhiteSpace(text[i + 1]))
                count++;
        }

        // Prose that never reaches terminal punctuation is still one sentence, not none.
        return count == 0 ? 1 : count;
    }
}
