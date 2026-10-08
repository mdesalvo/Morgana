using System.ComponentModel;
using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Model;
using Morgana.Contracts;
using Morgana.AI;
using Morgana.AI.Attributes;

namespace Alembic.Services;

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
            return ToolReply.Refused("Nothing recorded: an intent must have a name — it becomes a C# attribute argument and a prompt ID.");

        if (string.Equals(cleanName, ReservedFallbackIntent, StringComparison.OrdinalIgnoreCase))
            return ToolReply.Refused($"Nothing recorded: '{ReservedFallbackIntent}' is reserved. It is the intent the classifier "
                   + "falls back to when it cannot place a message and no agent may claim it. Call this again with a name from the domain.");

        if (draftStateService.Current?.Intents.Any(i =>
                string.Equals(i.Name, cleanName, StringComparison.OrdinalIgnoreCase)) == true)
            return ToolReply.Refused($"Nothing recorded: '{cleanName}' is already an intent of this domain, written before today. "
                   + "Two agents answering the same intent is a startup failure. Name what is different about this one.");

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

        return new ToolReply((revision ? $"'{cleanName}' revised on the map." : $"'{cleanName}' is on the map, in position {interviewState.Map.Count}.")
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
        int removed = interviewState.Map.RemoveAll(i =>
            string.Equals(i.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase));

        return removed > 0
            ? new ToolReply($"'{name}' is off the map.")
            : ToolReply.Refused($"Nothing dropped: '{name}' is not on the map.");
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
            return new ToolReply("The map is empty: no kind of request has been named yet.");

        return new ToolReply("The domain map as it stands. The descriptions are weighed against each other by the classifier, "
               + "and the buttons are read side by side by a user:\n"
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
            return ToolReply.Refused("No tool recorded: a tool must have a name, because the name is what pairs it with its C# method.");

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

        return new ToolReply((revision ? $"'{cleanName}' revised." : $"'{cleanName}' declared.")
               + (complaint.Length > 0 ? " " + complaint : string.Empty)
               + (string.IsNullOrWhiteSpace(description)
                   ? " It has no description and the description is what the model reads when it decides whether to call this tool at all."
                   : string.Empty));
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
            return ToolReply.Refused($"No parameter recorded: no tool named '{toolName}' has been declared yet.");

        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused("No parameter recorded: a parameter must have a name, because the adapter pairs it with the C# method's parameter by name and not by position.");

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

        return new ToolReply($"'{cleanName}' recorded on {tool.Name}."
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
    [Description("Removes a parameter from a tool. Also how a parameter moves: drop it and add it again in the position you want.")]
    [RequiresApproval(false)]
    public ToolReply DropToolParameter(
        [Description("The exact name of the tool.")] [ToolParameter(Records.ToolScope.Request)] string toolName,
        [Description("The exact name of the parameter to remove.")] [ToolParameter(Records.ToolScope.Request)] string parameterName)
    {
        if (Find(toolName) is not { } tool)
            return ToolReply.Refused($"Nothing dropped: no tool named '{toolName}' has been declared.");

        int removed = tool.Parameters.RemoveAll(p =>
            string.Equals(p.Name, parameterName?.Trim(), StringComparison.Ordinal));

        return removed > 0
            ? new ToolReply($"'{parameterName}' dropped from {tool.Name}.")
            : ToolReply.Refused($"Nothing dropped: {tool.Name} has no parameter named '{parameterName}'.");
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
            return ToolReply.Refused($"No field recorded: no tool named '{toolName}' has been declared yet.");

        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused("No field recorded: a field must have a name, because the record's property is paired with it by name.");

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

        return new ToolReply($"'{cleanName}' recorded on what {tool.Name} hands back."
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
            return ToolReply.Refused($"Nothing dropped: no tool named '{toolName}' has been declared.");

        int removed = tool.Returns.RemoveAll(r =>
            string.Equals(r.Name, name?.Trim(), StringComparison.Ordinal));

        return removed > 0
            ? new ToolReply($"'{name}' dropped from what {tool.Name} hands back.")
            : ToolReply.Refused($"Nothing dropped: {tool.Name} hands back no field named '{name}'.");
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

        int removed = interviewState.Agent.Tools.RemoveAll(t =>
            string.Equals(t.Name, cleanName, StringComparison.Ordinal));

        if (removed == 0)
            return ToolReply.Refused($"Nothing dropped: no tool named '{toolName}' has been declared.");

        List<string> orphaned =
        [.. interviewState.Agent.Workflows.SelectMany(workflow => workflow.Steps
                .Where(step => step.Tools.Contains(cleanName, StringComparer.Ordinal))
                .Select(step => $"'{cleanName}' is still named by step '{step.Name}' of workflow '{workflow.Name}', which is left without it."))];

        return new ToolReply($"'{toolName}' dropped, with its parameters and what it hands back."
               + (orphaned.Count > 0 ? " " + string.Join(" ", orphaned) : string.Empty));
    }

    /// <summary>
    /// Returns the toolkit as it currently stands.
    /// </summary>
    [Description("Returns the agent's tools, their parameters and what each hands back.")]
    [RequiresApproval(false)]
    public ToolReply GetToolkit()
    {
        if (interviewState.Agent.Tools.Count == 0)
            return new ToolReply("This agent declares no tools yet. That is a legal end interviewState — an agent whose tools "
                   + "all arrive from an MCP server declares none here — but it must be a conclusion you reached by asking.\n\n"
                   + DescribeWorkflows(interviewState.Agent.Workflows));

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

        return new ToolReply("The toolkit as it stands:\n" + string.Join("\n", rendered)
               + "\n\n" + DescribeWorkflows(interviewState.Agent.Workflows));
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
            return ToolReply.Refused("No workflow recorded: a workflow must have a name, because the model starts it by that name.");

        // The name is what the launcher function is named after, so a workflow kept under one that has
        // to change would survive the corrected call as a second workflow.
        string complaint = IdentifierComplaint(cleanName, "workflow name", pascalCase: true);

        if (complaint.Length > 0)
            return ToolReply.Refused("No workflow recorded. " + complaint);

        WorkflowDraft? existing = FindWorkflow(cleanName);
        WorkflowDraft workflow = existing ?? new WorkflowDraft { Name = cleanName, Origin = Provenance.Authored };
        workflow.Description = description?.Trim();

        if (existing is null)
            interviewState.Agent.Workflows.Add(workflow);

        return new ToolReply((existing is not null ? $"'{cleanName}' revised." : $"'{cleanName}' declared.")
               + (string.IsNullOrWhiteSpace(description)
                   ? " It has no description and the description is what the model reads when it decides whether to start this workflow at all."
                   : string.Empty));
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
            return ToolReply.Refused($"No step recorded: no workflow named '{workflow}' has been declared yet.");

        string cleanName = (name ?? string.Empty).Trim();

        if (cleanName.Length == 0)
            return ToolReply.Refused("No step recorded: a step must have a name.");

        List<string> stepTools = [.. (tools ?? []).Select(tool => tool?.Trim() ?? string.Empty)
                                                  .Where(tool => tool.Length > 0)
                                                  .Distinct(StringComparer.Ordinal)];
        List<string> complaints = [];

        string identifierComplaint = IdentifierComplaint(cleanName, "step name", pascalCase: true);

        if (identifierComplaint.Length > 0)
            complaints.Add(identifierComplaint);

        // An agent that acquires tools from an MCP server names tools nothing here can see, so only
        // an agent with none of those can have a tool judged absent on the spot.
        if (interviewState.Agent.Code.MCPServers.Count == 0)
            foreach (string tool in stepTools.Where(tool => Find(tool) is null))
                complaints.Add($"But this agent declares no tool named '{tool}'.");

        if (stepTools.Count == 0)
            complaints.Add("But a step offers at least one tool.");

        if (complaints.Count > 0)
            return ToolReply.Refused("No step recorded. " + string.Join(" ", complaints) + " Call again with the step corrected.");

        Records.WorkflowStep recorded = new(cleanName, stepTools);
        int position = owner.Steps.FindIndex(step => string.Equals(step.Name, cleanName, StringComparison.Ordinal));

        if (position >= 0)
            owner.Steps[position] = recorded;
        else
            owner.Steps.Add(recorded);

        return new ToolReply(position >= 0
            ? $"Step '{cleanName}' of '{owner.Name}' revised."
            : $"Step '{cleanName}' added to '{owner.Name}' as step {owner.Steps.Count}.");
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
            return ToolReply.Refused($"Nothing dropped: no workflow named '{workflow}' has been declared.");

        int removed = owner.Edges.RemoveAll(edge => IsEdge(edge, source, tool, failure));

        return removed > 0
            ? new ToolReply($"The {(failure ? "failure " : string.Empty)}edge of '{tool}' at '{source}' dropped from '{owner.Name}'.")
            : ToolReply.Refused($"Nothing dropped: '{owner.Name}' has no {(failure ? "failure " : string.Empty)}edge for '{tool}' at '{source}'.");
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
            return ToolReply.Refused($"No edge recorded: no workflow named '{workflow}' has been declared yet.");

        string cleanSource = (source ?? string.Empty).Trim();
        string cleanTarget = (target ?? string.Empty).Trim();
        string cleanTool = (tool ?? string.Empty).Trim();
        List<string> complaints = [];

        Records.WorkflowStep? sourceStep = owner.Steps.FirstOrDefault(step => string.Equals(step.Name, cleanSource, StringComparison.Ordinal));

        if (sourceStep is null)
            complaints.Add($"But '{owner.Name}' has no step named '{source}'.");
        else if (!sourceStep.Tools.Contains(cleanTool, StringComparer.Ordinal))
            complaints.Add($"But '{cleanTool}' is not a tool of step '{cleanSource}'.");

        if (!owner.Steps.Any(step => string.Equals(step.Name, cleanTarget, StringComparison.Ordinal)))
            complaints.Add($"But '{owner.Name}' has no step named '{target}'.");

        if (complaints.Count > 0)
            return ToolReply.Refused("No edge recorded. " + string.Join(" ", complaints) + " Call again with the edge corrected.");

        List<string> carried = [.. (carrying ?? []).Select(name => name?.Trim() ?? string.Empty)
                                                   .Where(name => name.Length > 0)
                                                   .Distinct(StringComparer.Ordinal)];
        Records.WorkflowEdge recorded = new(cleanSource, cleanTarget, cleanTool, onFailure, carried);
        int position = owner.Edges.FindIndex(edge => IsEdge(edge, cleanSource, cleanTool, onFailure));

        if (position >= 0)
            owner.Edges[position] = recorded;
        else
            owner.Edges.Add(recorded);

        return new ToolReply($"The {(onFailure ? "failure " : string.Empty)}edge of '{cleanTool}' at '{cleanSource}' "
               + (position >= 0 ? "revised" : "added")
               + $" in '{owner.Name}': it leads to '{cleanTarget}'.");
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
            return ToolReply.Refused($"Nothing dropped: no workflow named '{workflow}' has been declared.");

        string cleanStep = step?.Trim() ?? string.Empty;
        int removed = owner.Steps.RemoveAll(candidate => string.Equals(candidate.Name, cleanStep, StringComparison.Ordinal));

        if (removed == 0)
            return ToolReply.Refused($"Nothing dropped: '{owner.Name}' has no step named '{step}'.");

        // An edge whose end is gone would name a step that no longer exists.
        int edgesRemoved = owner.Edges.RemoveAll(edge =>
            string.Equals(edge.Source, cleanStep, StringComparison.Ordinal) || string.Equals(edge.Target, cleanStep, StringComparison.Ordinal));

        return new ToolReply($"Step '{step}' dropped from '{owner.Name}'"
               + (edgesRemoved > 0 ? $" with {edgesRemoved} edge(s)." : "."));
    }

    /// <summary>
    /// Removes a workflow and every step of it.
    /// </summary>
    [Description("Removes a workflow and every step of it.")]
    [RequiresApproval(false)]
    public ToolReply DropWorkflow(
        [Description("The exact name of the workflow to remove.")] [ToolParameter(Records.ToolScope.Request)] string name)
    {
        int removed = interviewState.Agent.Workflows.RemoveAll(workflow =>
            string.Equals(workflow.Name, name?.Trim(), StringComparison.Ordinal));

        return removed > 0
            ? new ToolReply($"'{name}' dropped, with its steps.")
            : ToolReply.Refused($"Nothing dropped: no workflow named '{name}' has been declared.");
    }

    /// <summary>
    /// Returns the workflows as they currently stand.
    /// </summary>
    [Description("Returns the agent's workflows as they stand, step by step. Call it before completing the pass.")]
    [RequiresApproval(false)]
    public ToolReply GetWorkflows() => new ToolReply(DescribeWorkflows(interviewState.Agent.Workflows));

    /// <summary>
    /// Renders workflows as readable text: one block each, its steps in order with the tools, then one
    /// line per edge with the values it carries.
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
            return new ToolReply("Nothing settled yet: this agent has no intent.");

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

        return new ToolReply("Settled in the earlier passes and not yours to reopen:\n\n"
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
            List<string>? parsed = JsonSerializer.Deserialize<List<string>>(
                traits, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            List<string> words = [.. (parsed ?? []).Select(w => w.Trim()).Where(w => w.Length > 0)];

            if (words.Count == 0)
                return ToolReply.Refused("No words offered: the payload held none.");

            interviewState.PendingTraits.Clear();
            interviewState.PendingTraits.AddRange(words);

            return new ToolReply($"{words.Count} words will be drawn under your question, to be picked from freely. "
                   + "The text box stays open, so the answer may still come in the client's own words.");
        }
        catch (JsonException ex)
        {
            return ToolReply.Refused($"No words offered: the payload is not a JSON array of words ({ex.Message}).");
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
    [Description("Writes down one thing the client has just told you about how their work actually goes — not what you decided about it. Every step after this one opens holding what is on record for the agent in hand and for the business, so call it the moment an answer says something a later step would otherwise have to ask again: what they sell, who writes in and about what, what they open to answer it, what goes wrong, what they never do. One fact per call, in their own words. Never what you wrote into a section — that is already in the configuration and a second copy of it here is a second thing to keep true. Where what they just said contradicts something on record, say so in 'corrects' and the wrong one goes: a record nobody can correct is worse than no record, because every step after you will hold it and none of them can doubt it.")]
    [RequiresApproval(false)]
    public ToolReply NoteDomainFact(
        [Description("What this is about, in one or two of THEIR words — 'prices', 'deposits', 'custom cakes'. Facts about the same thing take the same subject: it is what another agent's step reads to decide whether to fetch this at all.")] [ToolParameter(Records.ToolScope.Request)] string subject,
        [Description("One short sentence about their work, in their vocabulary, never about the agent: 'the catalogue changes with the season', 'custom cakes are quoted at an appointment, never over the counter'. Standing on one entry of the map it is kept with that agent; drawing the map or closing the domain it is kept for the business as a whole.")] [ToolParameter(Records.ToolScope.Request)] string fact,
        [Description("Any part of the sentence already on record that this puts right, where they have just contradicted it — a reading taken off their upload most of all. Leave it out when nothing is being corrected.")] [ToolParameter(Records.ToolScope.Request)] string? corrects = null)
    {
        string written = fact.Trim();
        string about = subject.Trim();

        if (written.Length == 0 || about.Length == 0)
            return ToolReply.Refused("Nothing written down: a fact needs both a subject and a sentence.");

        List<KnownFact> kept = Memory;
        int dropped = corrects is { Length: > 0 } wrong ? Forget(kept, wrong) : 0;

        // What the client says stands over what Alembic read off their upload about the same
        // subject: a reading of somebody else's prose loses to the person whose shop it is.
        dropped += kept.RemoveAll(known =>
            known.Inferred && string.Equals(known.Subject, about, StringComparison.OrdinalIgnoreCase));

        if (kept.Any(known => string.Equals(known.Fact, written, StringComparison.OrdinalIgnoreCase)))
            return new ToolReply("That was already written down; nothing added.");

        if (kept.Count >= MemoryCeiling)
            return ToolReply.Refused($"Nothing written down: {kept.Count} facts already stand here, which is as many as "
                   + "are worth carrying into a question. Two of them have become the same fact in "
                   + "different words — drop one with DropDomainFact, or say what this one corrects.");

        kept.Add(new KnownFact(about, written, Inferred: false));

        return new ToolReply($"Written down under '{about}'"
               + (dropped > 0 ? $"; {dropped} thing(s) that said otherwise are gone" : string.Empty)
               + $". {kept.Count} thing(s) now stand on record here.");
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
        string wrong = fact.Trim();

        if (wrong.Length == 0)
            return ToolReply.Refused("Nothing dropped: the payload was empty.");

        int dropped = Forget(Memory, wrong);

        return dropped == 0
            ? ToolReply.Refused("Nothing here says that; nothing dropped.")
            : new ToolReply($"{dropped} thing(s) gone from the record. No step after this one will hold them.");
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
            return ToolReply.Refused($"There is no agent called '{named}' in this domain. The opening message lists them by name.");

        // What the map says about an agent nobody has opened yet is the whole of what is known about
        // it. It is worth more than a refusal: the routing sentence the client dictated is the
        // only account of that counter anybody has.
        if (agent is null || agent.Known.Count == 0)
            return new ToolReply(string.IsNullOrWhiteSpace(entry?.Description)
                ? $"Nothing is on record about how they work at '{named}'."
                : $"Nothing is on record yet about how they work at '{named}'. The map describes it as: {entry.Description}");

        return new ToolReply($"What is known about '{named}':\n"
               + string.Join("\n", agent.Known.Select(known =>
                   $"- {known.Subject}: {known.Fact}" + (known.Inferred ? " (read off their configuration, not said)" : string.Empty))));
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
    [Description("States, over the question you are about to ask, what this step adds to the agent in hand. Call it in the turn a step lands on, before the question, every time without exception: a step that lands with nothing but a question leaves the client working out what this screen is for from the question alone. Call it in no other turn — a confirmation or a follow-up is not a landing and what stands there would say the interview had moved on when it has not. It is not room for anything else: never an answer to what they just said, never an apology, never a correction of your own last turn. Those belong in the question.")]
    [RequiresApproval(false)]
    public ToolReply SetStepPlacing(
        [Description("One short sentence in the second person, naming what THEY will be able to do once this step is settled, in the things their work is actually made of — the orders, the counter, the people who write in. Name neither the agent nor the step: both already stand lit on the screen. Where the sentence needs a subject it is the part of their work this agent stands for, in their own words. Two things it is never: a line about what software becomes able to do ('this is where the catalogue stops being just words and becomes something you can really consult' is a brochure and tells a baker nothing) or a restatement of why this stage exists, which is written above for you and not for them. The test is simple: if the same sentence could open this step for a garage, a vet and a bank alike, it places nothing. Never place it at the expense of the steps behind it: what those settled is already their configuration, so a step announcing that things now finally become real tells them the screens they have just filled in were a game.")] [ToolParameter(Records.ToolScope.Request)] string placing)
    {
        string written = placing.Trim();

        if (written.Length == 0)
            return ToolReply.Refused("Nothing placed: the payload was empty.");

        interviewState.PendingPlacing = written;

        return new ToolReply("That will stand above the question, quieter than it and apart from it. It is the only "
               + "thing on the screen telling them what this step is for, so the question itself "
               + "need not say it again.");
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
            return ToolReply.Refused("Nothing shown: the payload was empty.");

        interviewState.PendingQuoted = prose;

        return new ToolReply("That will stand on its own above your question, exactly as you wrote it. Your own "
               + "sentence should introduce it and never contain it: they are approving these words, "
               + "so what they read has to be only these words.");
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
        string written = example.Trim().Trim('"').Trim();

        if (written.Length == 0)
            return ToolReply.Refused("No example put in the box: the payload was empty.");

        interviewState.PendingExample = written;

        return new ToolReply("The example will stand in the answer box under your question, greyed and goes the "
               + "moment they answer. It is the only thing on the screen telling them how long an "
               + "answer is worth writing.");
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
            return ToolReply.Refused("No button attached: it needs both a label to read and the answer it sends.");

        interviewState.PendingChoice = new QuickReply("agree", label.Trim(), value.Trim());

        return new ToolReply("The button will be drawn under your question. "
               + "The text box stays open, so the answer may still come in the client's own words.");
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
    [Description("Returns every agent of the domain whole — what it is for, what its tools reach, how it goes about the work and the colleagues it may already ask. Call it before your first question: an edge is a claim about two agents at once and neither end of it is visible from the other.")]
    [RequiresApproval(false)]
    public ToolReply GetDomainAgents()
    {
        List<AgentDraft> agents = [.. (draftStateService.Current?.Agents ?? []).Where(a => !string.IsNullOrWhiteSpace(a.ID))];

        if (agents.Count == 0)
            return new ToolReply("The domain holds no agent yet: there is nothing that could ask anything of anything.");

        IEnumerable<string> rendered = agents.Select(a =>
            $"- {a.ID}\n    what it is for: {AgentRows.Plain(a.Target) ?? "(nothing said)"}"
            + $"\n    what it answers for, in the words a colleague reads: {AgentRows.Plain(a.Territory) ?? "(nothing said)"}"
            + $"\n    what it can reach: {(a.Tools.Count > 0 ? string.Join(", ", a.Tools.Select(t => t.Name ?? "(unnamed)")) : "no tool of its own")}"
            + $"\n    how it goes about it: {AgentRows.Plain(a.Instructions) ?? "(nothing said)"}"
            + $"\n    colleagues it may already ask: {PeerNaming.Describe(a.Code.Consults)}");

        return new ToolReply("The domain as it stands, every agent whole:\n" + string.Join("\n", rendered));
    }

    /// <summary>
    /// Returns the colleagues declared this step, none of them in the domain yet.
    /// </summary>
    [Description("Returns the edges declared so far this step, none of which is in the domain until the client agrees to the set. Read it back before settling: what you are asking them to agree to is the set, not the last edge.")]
    [RequiresApproval(false)]
    public ToolReply GetConsultations()
    {
        if (interviewState.Colleagues.Count == 0)
            return new ToolReply("Nothing declared yet this step. A domain where no agent needs a colleague is an ordinary domain, "
                   + "and settling the step with none is a legitimate answer.");

        return new ToolReply("Declared so far, waiting on the client's word:\n"
               + string.Join("\n", interviewState.Colleagues.Select(c =>
                   $"- {c.Asking} may ask {c.Asked}"
                   + (c.AskingTarget is null ? string.Empty : $" (and {c.Asking}'s Target was rewritten with it)")
                   + (c.AskedInstructions is null ? string.Empty : $" (and {c.Asked}'s own instructions were reconciled too)"))));
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
    [Description("Lets one agent put a question to another and rewrites the prose that would otherwise forbid it. Both in the same call, because either alone is a defect: the licence without the prose hands an agent a colleague its own instructions tell it not to use and the prose without the licence promises a question it has no way to ask.")]
    [RequiresApproval(false)]
    public ToolReply DeclareConsultation(
        [Description("The intent name of the agent that gains the colleague — the one whose customer asks something its own tools cannot answer.")] [ToolParameter(Records.ToolScope.Request)] string asking,
        [Description("The intent name of the colleague it may ask. It must be another agent of this domain, never the asking one itself.")] [ToolParameter(Records.ToolScope.Request)] string asked,
        [Description("The asking agent's INSTRUCTIONS section, rewritten whole: every sentence that still holds kept as it is and the boundary about this subject changed from a refusal or a hand-off ('that's another bench, go there') to a plain fact about this agent's OWN work. Say nothing about the colleague — not its name, its agent or its territory and not that this agent can reach it: the framework appends the colleague's own statement of all that to this prompt. No section label; that is added for you. Never a rule about when or how to consult.")] [ToolParameter(Records.ToolScope.Request)] string askingInstructions,
        [Description("The colleague's INSTRUCTIONS section, rewritten whole and ONLY where its own words would have it refuse what it is now being asked for. Leave it out otherwise, which is the ordinary case: what an agent will not say to a customer is not automatically what it will not tell a colleague and the framework already governs how that turn is answered.")] [ToolParameter(Records.ToolScope.Request)] string? askedInstructions = null,
        [Description("The asking agent's TARGET section, rewritten whole and ONLY when the sentence that turns this subject away is stated there rather than in its Instructions — which is where a boundary most often lives, since a Target says what the agent does and, existentially, what it does not. Keep every other sentence exactly as it stands and change that one the same way: from a refusal or a hand-off to a plain fact about this agent's OWN work, saying nothing about the colleague. Leave it out when the Target says nothing about this subject.")] [ToolParameter(Records.ToolScope.Request)] string? askingTarget = null)
    {
        string from = (asking ?? string.Empty).Trim();
        string to = (asked ?? string.Empty).Trim();

        if (from.Length == 0 || to.Length == 0)
            return ToolReply.Refused("Nothing declared: an edge needs both the agent that asks and the colleague it asks.");

        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            return ToolReply.Refused($"Nothing declared: '{from}' cannot consult itself. The startup registry refuses that pairing outright.");

        List<AgentDraft> agents = [.. draftStateService.Current?.Agents ?? []];

        if (!agents.Any(a => string.Equals(a.ID, from, StringComparison.OrdinalIgnoreCase)))
            return ToolReply.Refused($"Nothing declared: no agent of this domain answers '{from}'.");

        if (!agents.Any(a => string.Equals(a.ID, to, StringComparison.OrdinalIgnoreCase)))
            return ToolReply.Refused($"Nothing declared: no agent of this domain answers '{to}'.");

        if (string.IsNullOrWhiteSpace(askingInstructions))
            return ToolReply.Refused($"Nothing declared: '{from}' needs its Instructions rewritten in the same call. An agent handed a "
                   + "colleague while its own prose still says the subject belongs elsewhere and to stop there is being "
                   + "given two orders and the flat one wins.");

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

        return new ToolReply((existing is null ? $"'{from}' may now ask '{to}'." : $"The edge from '{from}' to '{to}' was revised.")
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
                   : string.Empty));
    }

    /// <summary>
    /// Takes back an edge declared this step.
    /// </summary>
    /// <remarks>
    /// The Instructions that came with it go too. They were written to admit a colleague that is no
    /// longer there and leaving them in the domain would be the mirror of the defect the edge
    /// exists against: prose licensing a question the agent has no function to ask.
    /// </remarks>
    [Description("Takes back an edge declared this step and the Instructions declared with it — they were written to admit a colleague that is no longer there.")]
    [RequiresApproval(false)]
    public ToolReply DropConsultation(
        [Description("The intent name of the agent that was to do the asking.")] [ToolParameter(Records.ToolScope.Request)] string asking,
        [Description("The intent name of the colleague.")] [ToolParameter(Records.ToolScope.Request)] string asked)
    {
        int removed = interviewState.Colleagues.RemoveAll(c =>
            string.Equals(c.Asking, asking?.Trim(), StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Asked, asked?.Trim(), StringComparison.OrdinalIgnoreCase));

        return removed > 0
            ? new ToolReply($"Dropped: '{asking}' will not ask '{asked}' and the prose declared with it is dropped too.")
            : ToolReply.Refused($"Nothing dropped: no edge from '{asking}' to '{asked}' was declared this step.");
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
        IEnumerable<string> written = (draftStateService.Current?.Intents ?? [])
            .Where(i => !string.Equals(i.Name, interviewState.Intent.Name, StringComparison.OrdinalIgnoreCase))
            .Select(i => $"- {i.Name}: {i.Description} (already in the domain)");

        IEnumerable<string> planned = interviewState.Map
            .Where(i => !ReferenceEquals(i, interviewState.Intent))
            .Select(i => $"- {i.Name}: {i.Description} (on the map, not written yet)");

        List<string> all = [.. written, .. planned];

        return new ToolReply(all.Count == 0
            ? "Nothing else claims a route: this is the only intent and nothing can collide with it."
            : "The descriptions the classifier weighs this one against:\n" + string.Join("\n", all));
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
            return new ToolReply("Nothing to compose yet: the agent has no target.");

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
            ? "This is the whole of what this agent's model will read:\n\n" + recap.SystemPrompt
            : "Morgana's own layer above this one has not changed since you read it. This is the part "
              + "that is yours, as her composer lays it out:\n\n" + recap.SystemPrompt[domain..]);
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
        return new ToolReply(CardProjection.Render(interviewState.Intent, interviewState.Agent));
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
        string written = (description ?? string.Empty).Trim();

        if (written.Length == 0)
            return ToolReply.Refused("Nothing changed: an intent with no description is one the classifier cannot route to.");

        interviewState.Intent.Description = written;

        return new ToolReply($"'{interviewState.Intent.Name}' now routes on: {written}");
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
            return new ToolReply("Nothing to check yet: the intent has no name.");

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

        return new ToolReply(findings.Count == 0
            ? "Nothing to report: every deterministic check passes for this agent."
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
            return ToolReply.Refused("Not completed: this section is reopened and the client has not said a word about it yet. "
                   + "State what it says today, attach the choice that agrees with it and settle it on their answer.");

        IReadOnlyList<string> missing = interviewState.Missing();

        if (missing.Count > 0)
            return ToolReply.Refused($"Not completed: {string.Join(", ", missing)} still unset. "
                   + (missing.Count == 1 ? "Set it and call this again." : "Set them and call this again."));

        interviewState.ReadyForReview = true;

        return new ToolReply("This pass is settled. Say it is done and what comes next: "
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
               });
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
