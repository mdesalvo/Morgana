using System.ComponentModel;
using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;

namespace Alembic.Tools;

/// <summary>The tools of the pass that declares an agent's tools.</summary>
public sealed class AgentToolkitTool : InterviewTool
{
    /// <summary>The scope of a parameter Morgana resolves from the session's own context variables.</summary>
    private const string ContextScope = Constants.Scopes.Context;

    /// <summary>The scope of a parameter the agent obtains from the user in conversation.</summary>
    private const string RequestScope = Constants.Scopes.Request;

    /// <summary>Binds the toolset to one interview.</summary>
    /// <param name="interviewState">The interview these tools write into.</param>
    /// <param name="draftStateService">The domain being built or evolved.</param>
    /// <param name="draftValidationService">The checks decidable by reading the Draft.</param>
    /// <param name="recapService">Composes the prompt the authored agent will really read.</param>
    public AgentToolkitTool(
        InterviewState interviewState,
        IDraftStateService draftStateService,
        IDraftValidationService draftValidationService,
        IRecapService recapService)
        : base(interviewState, draftStateService, draftValidationService, recapService)
    {
    }

    /// <inheritdoc />
    protected override string NextStep => "what this agent is the one to be asked about, which is what another agent of theirs reads before it asks.";

    /// <inheritdoc />
    [Description("Returns the agent as it currently stands: the intent it answers, what it is for, where it stops, how it sounds and what it can reach. Each step runs with a fresh memory and only the configuration carries over, so this is the whole of what you know about it. On an agent already in the client's domain, all of it was written before today.")]
    public override string GetAgentSoFar() => base.GetAgentSoFar();

    /// <inheritdoc />
    [Description("Returns the toolkit as it stands, tools and parameters together. Call it before completing the pass.")]
    public override string GetToolkit() => base.GetToolkit();

    /// <summary>
    /// Opens a tool or revises the description of one already open, keeping its parameters.
    /// A tool's contract is settled over several turns, so re-declaring it to sharpen the description must not empty it.
    /// </summary>
    [Description("Opens a tool or revises the description of one already open — revising keeps its parameters. Declare it as soon as the action is described, before you know its inputs.")]
    public string DeclareTool(
        [Description("PascalCase, no spaces or punctuation, a verb the agent performs (e.g. 'GetInvoices', 'CancelBooking'). It becomes a C# method name verbatim.")] string name,
        [Description("What the tool does, what it returns and WHEN to call it. Two to four sentences. Say what it does not cover where a neighbouring tool covers it. A description that names only the action leaves the timing to guesswork.")] string description)
    {
        // The name as the model wrote it, without the spaces a payload carries at its ends.
        string cleanName = (name ?? string.Empty).Trim();

        // The name pairs the tool with its C# method, so a nameless tool could never be emitted.
        if (cleanName.Length == 0)
            return "No tool recorded: a tool must have a name, because the name is what pairs it with its C# method.";

        // The tool already open under that name; otherwise a new one Alembic authors.
        ToolDraft? existing = Find(cleanName);
        bool revision = existing is not null;

        ToolDraft tool = existing ?? new ToolDraft { Name = cleanName, Origin = Provenance.Authored };
        tool.Description = description?.Trim();

        // Only a new tool joins the toolkit; a revised one keeps its place in it.
        if (!revision)
            interviewState.Agent.Tools.Add(tool);

        // Reported, never rewritten: the name is domain vocabulary and a silent correction leaves
        // Alembic telling the client one word while the configuration carries another.
        string complaint = IdentifierComplaint(cleanName, "tool name", pascalCase: true);

        // A tool without a description is one the model cannot decide to call, so the reply says so.
        return (revision ? $"'{cleanName}' revised." : $"'{cleanName}' declared.")
               + (complaint.Length > 0 ? " " + complaint : string.Empty)
               + (string.IsNullOrWhiteSpace(description)
                   ? " It has no description and the description is what the model reads when it decides whether to call this tool at all."
                   : string.Empty);
    }

    /// <summary>
    /// Adds a parameter to a tool or revises one already there, in place.
    /// The declaration order becomes the C# method's parameter order, which cannot put a required parameter after an optional one.
    /// </summary>
    /// <param name="toolName">The already-declared tool this parameter belongs to.</param>
    /// <param name="name">camelCase — becomes the C# method's parameter name, matched by name not position.</param>
    /// <param name="description">What the model reads when deciding what to pass here.</param>
    /// <param name="scope">
    /// <c>"context"</c> if Morgana resolves it from the session, <c>"request"</c> if the agent must
    /// obtain it in conversation or empty/<c>"none"</c> for a value the agent authors itself.
    /// </param>
    /// <param name="required">Whether the call fails without it — required parameters must precede optional ones.</param>
    /// <param name="shared">Whether a resolved context value is published for other agents to hydrate from; meaningful only with scope <c>"context"</c>.</param>
    [Description("Adds a parameter to a declared tool or revises one already there by name and in place. Declaration order becomes the C# parameter order, which cannot put a required parameter after an optional one.")]
    public string SetToolParameter(
        [Description("The exact name of a tool you have already declared.")] string toolName,
        [Description("camelCase, no spaces or punctuation (e.g. 'invoiceId'). It becomes the C# parameter name verbatim.")] string name,
        [Description("What the value is and what a good one looks like, with an example where one helps. This reaches the agent's model through the tool's JSON schema and nowhere else. Never restate here that context values are looked up before being asked — Morgana says that herself, twice.")] string description,
        [Description("'request' if the value is stated in the conversation, 'context' if the agent should already hold it from earlier, 'none' if the agent composes the value itself. Infer it from the setup; do not ask parameter by parameter.")] string scope,
        [Description("Whether the agent must supply this value on every call. Optional parameters come after the required ones.")] bool required,
        [Description("Whether the resolved value is published to the whole conversation, so other agents use it without asking again. Only meaningful alongside scope 'context'. True for an identity the domain establishes once, false for one agent's own working value.")] bool shared)
    {
        // A parameter belongs to a tool already declared, which the model has to open first.
        if (Find(toolName) is not { } tool)
            return $"No parameter recorded: no tool named '{toolName}' has been declared yet.";

        // The name as the model wrote it, without the spaces a payload carries at its ends.
        string cleanName = (name ?? string.Empty).Trim();

        // The adapter pairs a parameter by name, so a nameless one could never be bound.
        if (cleanName.Length == 0)
            return "No parameter recorded: a parameter must have a name, because the adapter pairs it with the C# method's parameter by name and not by position.";

        // "none" is spelled out because a model asked for an empty string tends to send the word.
        string cleanScope = (scope ?? string.Empty).Trim().ToLowerInvariant();
        string? resolvedScope = cleanScope switch
        {
            // The two scopes the framework reads.
            ContextScope => ContextScope,
            RequestScope => RequestScope,

            // A value the agent authors itself.
            "" or "none" or "null" => null,

            // Anything else is kept and reported below, never coerced into a scope the model did not mean.
            _ => cleanScope
        };

        // The parameter already on the tool under that name, matched with the case the C# keeps.
        ToolParameterDraft? existing = tool.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, cleanName, StringComparison.Ordinal));

        // Revised in place, so its position in the signature survives; a new one is added at the end.
        bool revision = existing is not null;
        ToolParameterDraft parameter = existing ?? new ToolParameterDraft { Name = cleanName };

        parameter.Description = description?.Trim();
        parameter.Scope = resolvedScope;
        parameter.Required = required;
        parameter.Shared = shared;

        // Only a new parameter joins the signature.
        if (!revision)
            tool.Parameters.Add(parameter);

        // Every problem the parameter now has, reported together so the model fixes them in one call.
        List<string> complaints = [];

        // The name becomes a C# parameter, camelCase as the framework's own are.
        string identifier = IdentifierComplaint(cleanName, "parameter name", pascalCase: false);
        if (identifier.Length > 0)
            complaints.Add(identifier);

        // A scope the framework does not read would leave the parameter with no source.
        if (resolvedScope is not null and not ContextScope and not RequestScope)
            complaints.Add($"'{cleanScope}' is not a scope: a parameter resolving an input declares '{ContextScope}' or '{RequestScope}' and one carrying a value you author yourself declares none.");

        // Sharing publishes a resolved context variable, so it means nothing on any other scope.
        if (shared && resolvedScope != ContextScope)
            complaints.Add($"Shared only means something alongside scope '{ContextScope}': it publishes a resolved context variable so other agents can hydrate from it.");

        // The order is the signature, so an optional parameter followed by a required one is not a
        // preference: C# could not declare that signature.
        int firstOptional = tool.Parameters.FindIndex(p => !p.Required);
        if (firstOptional >= 0 && tool.Parameters.Skip(firstOptional).Any(p => p.Required))
            complaints.Add("A required parameter now sits after an optional one, which C# cannot declare. Reorder them by dropping and re-adding or make the earlier one required.");

        // Recorded either way: the complaints are what the model corrects next.
        return $"'{cleanName}' recorded on {tool.Name}."
               + (complaints.Count > 0 ? " " + string.Join(" ", complaints) : string.Empty);
    }

    /// <summary>Removes a parameter from a tool.</summary>
    [Description("Removes a parameter from a tool. Also how a parameter moves: drop it and add it again in the position you want.")]
    public string DropToolParameter(
        [Description("The exact name of the tool.")] string toolName,
        [Description("The exact name of the parameter to remove.")] string parameterName)
    {
        // A tool not declared has no parameter to drop.
        if (Find(toolName) is not { } tool)
            return $"Nothing dropped: no tool named '{toolName}' has been declared.";

        // Matched with the case the C# keeps.
        int removed = tool.Parameters.RemoveAll(p =>
            string.Equals(p.Name, parameterName?.Trim(), StringComparison.Ordinal));

        // A drop that found nothing is said plainly, so the model does not believe the signature changed.
        return removed > 0
            ? $"'{parameterName}' dropped from {tool.Name}."
            : $"Nothing dropped: {tool.Name} has no parameter named '{parameterName}'.";
    }

    /// <summary>Removes a tool and everything on it.</summary>
    [Description("Removes a tool and every parameter on it. Use it when the agent turns out to have no such job or when you have split one action into two tools that cannot be told apart.")]
    public string DropTool(
        [Description("The exact name of the tool to remove.")] string toolName)
    {
        // Matched with the case the C# keeps.
        int removed = interviewState.Agent.Tools.RemoveAll(t =>
            string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));

        // A drop that found nothing is said plainly, so the model does not believe the toolkit changed.
        return removed > 0
            ? $"'{toolName}' dropped, with its parameters."
            : $"Nothing dropped: no tool named '{toolName}' has been declared.";
    }

    /// <inheritdoc />
    [Description("Returns everything wrong with this agent that can be decided without a model: a name that cannot be a C# identifier, a tool colliding with one Morgana already gives every agent, a required parameter behind an optional one. Fix what it reports rather than explaining it away.")]
    public override string GetFindings() => base.GetFindings();

    /// <inheritdoc />
    [Description("Shows the client, word for word, the prose you have just written for their agent. Call it on the turn that asks whether a section is right, before the question: what they are approving is those exact words, so those words stand on their own and your own sentence introduces them rather than containing them. 'I have written a fond and careful voice that follows…' is one stretch of prose in which nothing marks where you stop speaking and their agent's text begins. An approval given to that approves nothing in particular. Only what is actually written — never a paraphrase, never a summary of it.")]
    public override string ShowWhatIsWritten(
        [Description("The section's prose exactly as it now stands, with nothing added around it and no quotation marks of your own.")] string written)
        => base.ShowWhatIsWritten(written);

    /// <inheritdoc />
    [Description("Puts a worked example in the answer box under the question you are about to ask, greyed out, for them to take and cut about or to type over. Call it before every question that asks them for something new — the one that opens the step and every follow-up after it — so the box is never empty under an open question. Not on the confirming turn that carries the button: there the answer that adds nothing already stands on the screen.")]
    public override string SetExample(
        [Description("An answer somebody in THEIR trade might have given to the question you are about to ask, cut to the size of that question — forty to sixty words where it opens a step, ten to twenty where it follows one up — in their vocabulary and about the agent you are building — never about another business, never a description of what to write and never the question itself put into their mouth: an example that says what the question says adds nothing to the screen it stands on. It teaches the register, the length and the level of detail at once. Send it unquoted: it stands in the box as text they can send as it is, so quotation marks around it arrive as part of their own answer. Make it close enough to their work to be worth editing and specific enough that it cannot be agreed with as it stands: an example that fits them exactly is one they will press past without adding what only they know.")] string example)
        => base.SetExample(example);

    /// <inheritdoc />
    [Description("Attaches to the question you are about to ask the one button that answers it without adding anything — agreement or that there is nothing to add — so a client in that position settles the turn with one press instead of a typed sentence. Call it BEFORE writing the question — the button renders below it. In this pass it belongs to the turn that states an inference back and to no other. EVERY turn that states back what you have just written carries it, without exception: that turn's whole question is whether they agree. A client with nothing to object to should not have to type a sentence to say so.")]
    public override string SetChoice(
        [Description("What the client reads on the button. It carries the answer that adds nothing and has to answer the question AS YOU PUT IT — 'That's everything' where you asked whether that is everything, 'Nothing missing' where you asked whether anything is missing, 'No, nothing like that' where you asked whether there is something the agent should never do.")] string label,
        [Description("The complete answer the button sends, in the client's register, reading as something they would plausibly have typed.")] string value)
        => base.SetChoice(label, value);

    /// <inheritdoc />
    [Description("Declares this pass settled. The declaration lives in this call and nowhere else: never write a marker, a token or a closing formula into your text to mean it.")]
    public override string SetPassCompleted() => base.SetPassCompleted();

    /// <summary>
    /// Finds a declared tool by exact name.
    /// The name becomes a C# method name, so two tools differing only in case are two tools here and a finding at startup.
    /// </summary>
    private ToolDraft? Find(string? toolName) =>
        interviewState.Agent.Tools.FirstOrDefault(t =>
            string.Equals(t.Name, toolName?.Trim(), StringComparison.Ordinal));
}