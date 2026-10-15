using Morgana.AI;

namespace Alembic.Models;

/// <summary>
/// The whole Morgana domain under construction: the single artifact the interview fills, the
/// validator checks, the recap composes and the emit reads.
/// </summary>
/// <remarks>Every nullable string in the Draft means "not asked yet".</remarks>
public sealed class DomainDraft
{
    /// <summary>
    /// What is known about the client's business as a whole: what they sell or do, how the place
    /// works, what is true of every counter in it.
    /// </summary>
    /// <remarks>It outlives any sitting and never enters an agent: it is what the questions are made of.</remarks>
    public List<KnownFact> Learned { get; set; } = [];

    /// <summary>
    /// Intent definitions, one per agent plus the framework's own <c>other</c>.
    /// </summary>
    public List<IntentDraft> Intents { get; set; } = [];

    /// <summary>
    /// Agent prompts, each keyed by an ID matching an intent name.
    /// </summary>
    public List<AgentDraft> Agents { get; set; } = [];

    /// <summary>
    /// What the migration report names its baseline by: a label for the archive that emitted it or the file name of a configuration imported by an earlier Alembic. Set on <see cref="Baseline"/> and <c>null</c> where there is none.
    /// </summary>
    public string? ImportedFrom { get; set; }

    /// <summary>
    /// When this Draft was created, UTC.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The domain exactly as the last archive emitted it, which is what the client's own C# was written against. <c>null</c> until an archive has been built or where none was resumed.
    /// </summary>
    /// <remarks>Its own baseline is always <c>null</c>: the report compares two Drafts, never a chain.</remarks>
    public DomainDraft? Baseline { get; set; }

    /// <summary>
    /// The interview as it stood when this file was written, or <c>null</c> when none was under way.
    /// </summary>
    /// <remarks>It holds the configuration in hand and never the conversation, so resuming re-enters the step with a fresh agent.</remarks>
    public InterviewSitting? Sitting { get; set; }

    /// <summary>
    /// The name the classifier falls back to when it cannot place a message at all.
    /// </summary>
    public const string FallbackIntent = Constants.Intents.Other;

    /// <summary>
    /// Takes the fallback intent out of the domain, wherever it came from.
    /// </summary>
    /// <remarks>The fallback belongs to Morgana's classifier, so an uploaded configuration that carries one loses it and the client is told.</remarks>
    /// <returns>Whether a declaration was actually there to remove.</returns>
    public bool DropFallbackIntent()
    {
        // The domain's own intent list loses the fallback wherever the upload put it.
        bool dropped = Intents.RemoveAll(IsFallback) > 0;

        // The map of an interrupted sitting is a second place a domain declares its agents; the walk would open an agent on the fallback.
        if (Sitting is not null)
            dropped |= Sitting.Map.RemoveAll(IsFallback) > 0;

        // The caller tells the client only when something was actually removed.
        return dropped;
    }

    /// <summary>Whether this entry is the classifier's fallback and not an agent of the domain.</summary>
    private static bool IsFallback(IntentDraft intent) =>
        string.Equals(intent.Name, FallbackIntent, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An interview interrupted: where it had got to and what it had in hand.
/// </summary>
/// <remarks>The entries already accepted are in the domain itself and not here.</remarks>
public sealed class InterviewSitting
{
    /// <summary>
    /// Which step of an entry the interview stood on.
    /// </summary>
    public InterviewStep Pass { get; set; }

    /// <summary>
    /// Which entry of the map it stood on, or -1 while the map was still being drawn.
    /// </summary>
    public int At { get; set; } = -1;

    /// <summary>
    /// The map as dictated with the accepted entries included, since the interview walks it as one list.
    /// </summary>
    public List<IntentDraft> Map { get; set; } = [];

    /// <summary>
    /// The agent in hand, however far it had got.
    /// </summary>
    public AgentDraft Agent { get; set; } = new();

    /// <summary>
    /// The worked example standing in the answer box, if the question it belongs to still is.
    /// </summary>
    /// <remarks>A fresh example written by the turn that reopens the step wins over this one.</remarks>
    public string? Example { get; set; }

    /// <summary>
    /// What the agent in hand was before it left the domain, when this sitting is one section of an
    /// agent being revised and not a fresh entry being written.
    /// </summary>
    public AgentRevision? Revision { get; set; }

    /// <summary>
    /// The interview a correction set aside, so a tab closed mid-correction does not lose the rest of the map.
    /// </summary>
    public InterviewSitting? Resume { get; set; }
}

/// <summary>
/// An agent taken out of the domain to be worked on and what it needs to go back the way it came.
/// </summary>
/// <remarks>It exists only while a section is being rewritten: leafing through an agent creates none.</remarks>
public sealed class AgentRevision
{
    /// <summary>Where the intent stood in the domain's own list.</summary>
    public int IntentAt { get; set; } = -1;

    /// <summary>Where the agent stood in the domain's own list.</summary>
    public int AgentAt { get; set; } = -1;

    /// <summary>What the agent's provenance was before the edit opened.</summary>
    public Provenance Origin { get; set; }

    /// <summary>
    /// What the agent read when it left the domain, so leaving the edit can tell a rewrite from a look.
    /// </summary>
    /// <remarks>An agent that returns unchanged keeps its <see cref="Provenance.Imported"/> origin and stays out of the migration report.</remarks>
    public string? Was { get; set; }
}

/// <summary>
/// One intent under construction: what the classifier matches on and what the presenter offers.
/// </summary>
public sealed class IntentDraft
{
    /// <summary>
    /// Intent name. Matches the agent ID and the <c>[HandlesIntent]</c> argument.
    /// </summary>
    /// <remarks><c>null</c> until the mapping pass names this entry — never "no name".</remarks>
    public string? Name { get; set; }

    /// <summary>
    /// What the intent covers, in the classifier's own words. This is the single most
    /// collision-prone field in a domain: two descriptions that overlap produce a classifier
    /// collision, which is why the cross-agent coherence pass exists.
    /// </summary>
    /// <remarks><c>null</c> until asked.</remarks>
    public string? Description { get; set; }

    /// <summary>
    /// Human-facing label, used for the presenter's quick reply buttons.
    /// </summary>
    /// <remarks>Written for the whole map at once, since a label reads correctly only beside the others.</remarks>
    public string? Label { get; set; }

    /// <summary>
    /// Text sent on the user's behalf when the presenter's button for this intent is pressed.
    /// </summary>
    /// <remarks><c>null</c> until written alongside <see cref="Label"/>, for the same reason.</remarks>
    public string? DefaultValue { get; set; }

    /// <summary>
    /// Where this intent came from.
    /// </summary>
    public Provenance Origin { get; set; } = Provenance.Authored;
}

/// <summary>
/// One domain agent under construction: its prose, its toolkit and the facts about its C#
/// that <c>agents.json</c> does not carry.
/// </summary>
public sealed class AgentDraft
{
    /// <summary>
    /// Agent ID, matching an intent name.
    /// </summary>
    /// <remarks><c>null</c> until the entry is opened against a named intent from the map.</remarks>
    public string? ID { get; set; }

    /// <summary>
    /// What the client has said about the work this agent does, in their own words: the picture of
    /// their counter as the interview has painted it so far, step by step.
    /// </summary>
    /// <remarks>It holds what no section can carry (which screen they open, what goes wrong on a Saturday) and never enters the domain.</remarks>
    public List<KnownFact> Known { get; set; } = [];

    /// <summary>
    /// What the agent is for: its scope and its boundaries.
    /// </summary>
    /// <remarks><c>null</c> until the <c>AgentTarget</c> pass settles it.</remarks>
    public string? Target { get; set; }

    /// <summary>
    /// What falls to this agent, addressed to a colleague who might consult it.
    /// </summary>
    /// <remarks>It never enters this agent's own prompt: it is published on the agent's card for whoever holds a <c>consult_</c> function for it.</remarks>
    public string? Territory { get; set; }

    /// <summary>
    /// Domain-specific behavioural rules. Written after the toolkit, because they speak about it.
    /// </summary>
    /// <remarks><c>null</c> until the <c>AgentInstructions</c> pass runs.</remarks>
    public string? Instructions { get; set; }

    /// <summary>
    /// How the agent presents its own tools' output. Written after the toolkit, for the same reason.
    /// </summary>
    /// <remarks><c>null</c> until the <c>AgentFormatting</c> pass, the last one before the client lets the entry in.</remarks>
    public string? Formatting { get; set; }

    /// <summary>
    /// Tone and voice. Optional in the framework and optional here.
    /// </summary>
    /// <remarks><c>null</c> until the <c>AgentPersonality</c> pass and it may stay so once accepted, since a voice is optional.</remarks>
    public string? Personality { get; set; }

    /// <summary>
    /// BCP 47 language code, e.g. <c>"en-US"</c>.
    /// </summary>
    public string Language { get; set; } = "en-US";

    /// <summary>
    /// Prompt version string, for tracking iterations of the prose.
    /// </summary>
    public string Version { get; set; } = "1";

    /// <summary>
    /// The agent's toolkit.
    /// </summary>
    public List<ToolDraft> Tools { get; set; } = [];

    /// <summary>
    /// The agent's workflows: procedures whose steps the framework keeps in order, composed of the
    /// tools above and emitted as one class each.
    /// </summary>
    public List<WorkflowDraft> Workflows { get; set; } = [];

    /// <summary>
    /// AdditionalProperties entries that Alembic does not model, written back as they were read.
    /// </summary>
    public List<Dictionary<string, object>> UnmodelledProperties { get; set; } = [];

    /// <summary>
    /// The facts about this agent's C# that <c>agents.json</c> does not carry.
    /// </summary>
    public AgentCodeFacts Code { get; set; } = new();

    /// <summary>
    /// Where this agent came from.
    /// </summary>
    public Provenance Origin { get; set; } = Provenance.Authored;
}

/// <summary>
/// One tool under construction: the contract between the agent's prose and a C# method, declared on
/// that method and on the record it returns.
/// </summary>
public sealed class ToolDraft
{
    /// <summary>
    /// Tool name: the C# method name, exactly.
    /// </summary>
    /// <remarks><c>null</c> until the <c>AgentToolkit</c> pass declares the tool.</remarks>
    public string? Name { get; set; }

    /// <summary>
    /// What the tool does, which the model reads when it weighs whether to call it.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The tool's parameters, in the order the C# method declares them.
    /// </summary>
    public List<ToolParameterDraft> Parameters { get; set; } = [];

    /// <summary>
    /// Whether the tool runs only once the user has approved that very call, declared on the method as <c>[RequiresApproval]</c>.
    /// </summary>
    public bool RequiresExecutionApproval { get; set; }

    /// <summary>
    /// The button that offers the tool on the agent's menu, declared on the method as <c>[UserTool]</c>; <c>null</c> when the method is declared <c>[SystemTool]</c>.
    /// </summary>
    public Records.MenuEntry? MenuEntry { get; set; }

    /// <summary>
    /// The fields of the record the tool's method returns, in the order the record declares them.
    /// </summary>
    public List<ToolReturnDraft> Returns { get; set; } = [];

    /// <summary>
    /// Where this tool came from.
    /// </summary>
    public Provenance Origin { get; set; } = Provenance.Authored;
}

/// <summary>
/// One field of the record a tool returns, under construction.
/// </summary>
/// <remarks>
/// The field named <see cref="Constants.Workflows.FailureField"/> is the failure: it is the one that
/// holds a value only when the call failed, so no flag says so.
/// </remarks>
public sealed class ToolReturnDraft
{
    /// <summary>
    /// The name the model reads: camelCase, the record's property with its first letter lowered.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// What the field holds, as the model reads it in the result's schema.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The C# type of the record's property exactly as written there, such as <c>string</c>, <c>long?</c> or <c>List&lt;string&gt;</c>.
    /// </summary>
    /// <remarks><c>null</c> until it is asked: an uploaded domain written before the types were declared arrives with none.</remarks>
    public string? Type { get; set; }
}

/// <summary>
/// One workflow under construction: a procedure of one agent that the framework runs step by step.
/// </summary>
public sealed class WorkflowDraft
{
    /// <summary>
    /// What names the function that starts it, <c>Start</c> followed by this name; unique per agent.
    /// </summary>
    /// <remarks><c>null</c> until the <c>AgentWorkflows</c> pass declares the workflow.</remarks>
    public string? Name { get; set; }

    /// <summary>
    /// What the procedure does, offered to the model beside its name.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The steps in order; the first one is where the workflow starts.
    /// </summary>
    public List<Records.WorkflowStep> Steps { get; set; } = [];

    /// <summary>
    /// The transitions in declaration order; the properties the workflow carries are the distinct names across them.
    /// </summary>
    public List<Records.WorkflowEdge> Edges { get; set; } = [];

    /// <summary>
    /// The button that offers the workflow on the agent's menu, declared on its class as <c>[UserTool]</c>; <c>null</c> when the class is declared <c>[SystemTool]</c>.
    /// </summary>
    public Records.MenuEntry? MenuEntry { get; set; }

    /// <summary>
    /// Where this workflow came from.
    /// </summary>
    public Provenance Origin { get; set; } = Provenance.Authored;
}

/// <summary>
/// One tool parameter under construction.
/// </summary>
public sealed class ToolParameterDraft
{
    /// <summary>
    /// Parameter name: the C# method's parameter name, exactly.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// What the parameter is, which reaches the model's schema unchanged.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether the model must supply this parameter: the one that has no default value in the C# signature.
    /// </summary>
    /// <remarks>Only a request parameter may be optional, since a context value that nobody holds stops the tool.</remarks>
    public bool Required { get; set; } = true;

    /// <summary>
    /// <c>"context"</c> (resolved from the session's context variables) or <c>"request"</c>
    /// (obtained from the user).
    /// </summary>
    /// <remarks>Inferred once from what the system already knows about a user on arrival and never asked per parameter; <c>null</c> while unsettled.</remarks>
    public string? Scope { get; set; }

    /// <summary>
    /// Whether the resolved value is published to the conversation-scoped <c>shared_context</c>
    /// registry so other agents can hydrate from it. Only a <c>"context"</c> parameter may be shared.
    /// </summary>
    public bool Shared { get; set; }
}

/// <summary>
/// What an agent's C# declares and <c>agents.json</c> does not: class names, namespace, tier,
/// MCP servers.
/// </summary>
/// <remarks>A class name inferred from the agent ID is a proposal for the client to confirm and is flagged <see cref="Inferred"/>.</remarks>
public sealed class AgentCodeFacts
{
    /// <summary>
    /// Namespace of the generated classes.
    /// </summary>
    /// <remarks>Left <c>null</c> until asked, since a namespace follows from nothing the Draft holds.</remarks>
    public string? Namespace { get; set; }

    /// <summary>
    /// Proposes a class name from an intent name by the framework's convention: <c>billing</c> and
    /// <c>Agent</c> give <c>BillingAgent</c>. <c>null</c> when there is no name to go on.
    /// </summary>
    public static string? ProposeClassName(string? intentName, string suffix)
    {
        // A missing intent name leaves nothing to build a class name from.
        string bareName = intentName?.Trim() ?? string.Empty;

        // The class name is the intent capitalised plus the framework's suffix; a blank name yields no proposal.
        return bareName.Length > 0 ? $"{char.ToUpperInvariant(bareName[0])}{bareName[1..]}{suffix}" : null;
    }

    /// <summary>
    /// Name of the <c>MorganaAgent</c> subclass, e.g. <c>BillingAgent</c>.
    /// </summary>
    public string? AgentClassName { get; set; }

    /// <summary>
    /// Name of the <c>MorganaTool</c> subclass, e.g. <c>BillingTool</c>. <c>null</c> for an
    /// MCP-only agent, which is a legal configuration.
    /// </summary>
    public string? ToolClassName { get; set; }

    /// <summary>
    /// The tier the agent runs on, declared in C# via <c>[RequiresLLMTier]</c>.
    /// </summary>
    public Morgana.AI.Records.LLMTier? Tier { get; set; }

    /// <summary>
    /// MCP server declarations, one per <c>[UsesMCPServer]</c> attribute.
    /// </summary>
    public List<string> MCPServers { get; set; } = [];

    /// <summary>
    /// The colleagues this agent may put a question to, one entry per <c>[ConsultsAgent]</c>
    /// attribute: an intent and the instance publishing it where that is not this domain.
    /// </summary>
    /// <remarks>The framework refuses a second hop, so an edge is worth declaring only where the colleague answers out of its own books.</remarks>
    public List<Morgana.AI.Records.PeerReference> Consults { get; set; } = [];

    /// <summary>
    /// Whether the values above were inferred by Alembic and not stated by the client.
    /// True for a class name that Alembic proposed and the client has not confirmed yet.
    /// </summary>
    public bool Inferred { get; set; }
}
