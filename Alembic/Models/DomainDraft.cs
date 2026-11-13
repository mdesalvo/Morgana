using Morgana.AI;

namespace Alembic.Models;

/// <summary>
/// The whole Morgana domain under construction: the single artifact the interview fills, the validator checks, the recap composes and the emit reads.
/// It is the editing model beside the framework's immutable records: an interview in progress is incomplete, so every nullable string means "not asked yet";
/// where a shape is final it is the framework's own record.
/// </summary>
public sealed class DomainDraft
{
    /// <summary>
    /// What is known about the client's business as a whole: what they sell or do, how the place works, what is true of every counter in it.
    /// It is kept with the domain, since it outlives any sitting, and never written into an agent: it is what the questions are made of.
    /// </summary>
    public List<KnownFact> Learned { get; set; } = [];

    /// <summary>Intent definitions, one per agent; the classifier's own <c>other</c> is never among them.</summary>
    public List<IntentDraft> Intents { get; set; } = [];

    /// <summary>Agent prompts, each keyed by an ID matching an intent name.</summary>
    public List<AgentDraft> Agents { get; set; } = [];

    /// <summary>Name of the uploaded file this Draft was imported from, which the migration report names; <c>null</c> for a greenfield Draft.</summary>
    public string? ImportedFrom { get; set; }

    /// <summary>When this Draft was created or last imported into, UTC.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The domain exactly as it was uploaded, frozen at import, which the migration report diffs against; <c>null</c> for a greenfield Draft.
    /// It is a Draft so the diff compares like with like and it is saved with the rest so a report resumed the next day still has it; its own baseline is always null.
    /// </summary>
    public DomainDraft? Baseline { get; set; }

    /// <summary>
    /// The interview as it stood when this file was written; <c>null</c> when none was under way.
    /// It saves the configuration in hand and never the conversation, so closing the tab mid-agent loses nothing and resuming re-enters the step with a fresh session.
    /// </summary>
    public InterviewSitting? Sitting { get; set; }

    /// <summary>The name the classifier falls back to when it cannot place a message at all.</summary>
    public const string FallbackIntent = Constants.Intents.Other;

    /// <summary>
    /// Takes the fallback intent out of the domain, wherever it came from.
    /// It is the complement of the domain and belongs to Morgana's classifier, which refuses at startup a plugin declaring it, so a configuration carrying one loses it here.
    /// </summary>
    /// <returns>Whether a declaration was actually there to remove.</returns>
    public bool DropFallbackIntent()
    {
        // The domain's own intents, the first place a configuration can declare it.
        bool dropped = Intents.RemoveAll(IsFallback) > 0;

        // The map of an interrupted sitting is a second place: a sitting saved over such a configuration
        // would otherwise resume with an entry the walk opens an agent on.
        if (Sitting is not null)
            dropped |= Sitting.Map.RemoveAll(IsFallback) > 0;

        // The caller tells the client only when there was something to drop.
        return dropped;
    }

    /// <summary>Whether this entry is the classifier's fallback rather than an agent of the domain.</summary>
    private static bool IsFallback(IntentDraft intent) =>
        string.Equals(intent.Name, FallbackIntent, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// An interview interrupted: where it had got to and what it had in hand, every one a fact the state machine owns.
/// The entries already accepted are in the domain itself, where they belong the moment the client lets them in.
/// </summary>
public sealed class InterviewSitting
{
    /// <summary>Which step of an entry the interview stood on.</summary>
    public InterviewStep Pass { get; set; }

    /// <summary>Which entry of the map it stood on; -1 while the map was still being drawn.</summary>
    public int At { get; set; } = -1;

    /// <summary>The map as dictated, entries already accepted included, since half of the list would resume as a different domain.</summary>
    public List<IntentDraft> Map { get; set; } = [];

    /// <summary>The agent in hand, however far it had got.</summary>
    public AgentDraft Agent { get; set; } = new();

    /// <summary>
    /// The worked example standing in the answer box, kept for the turn that reopens this step.
    /// A fresh example written by that turn wins, exactly as it would have without a save in between.
    /// </summary>
    public string? Example { get; set; }

    /// <summary>What the agent in hand was before it left the domain, when this sitting corrects an agent rather than writing a fresh entry.</summary>
    public AgentRevision? Revision { get; set; }

    /// <summary>
    /// The interview a correction set aside, so a tab closed mid-correction does not lose the rest of the map.
    /// </summary>
    public InterviewSitting? Resume { get; set; }
}

/// <summary>
/// An agent taken out of the domain to be worked on and what it needs to go back the way it came: its place in both lists and its provenance.
/// An agent being edited is out of the configuration, or letting it back in would write it twice; its place keeps the client's order and its provenance
/// brings an imported agent back as revised. Reading an agent on the way past creates none.
/// </summary>
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
    /// An agent that goes back exactly as it came stays imported, or the migration report would list everything the client opened.
    /// </summary>
    public string? Was { get; set; }
}

/// <summary>
/// One intent under construction: what the classifier matches on and what the presenter offers.
/// </summary>
public sealed class IntentDraft
{
    /// <summary>Intent name, matching the agent ID and the <c>[HandlesIntent]</c> argument; <c>null</c> until the mapping pass names this entry.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// What the intent covers, in the classifier's own words; <c>null</c> until asked.
    /// It is the field most prone to collide, since two overlapping descriptions make a classifier collision.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The label of the button the presenter offers for this intent; <c>null</c> until the mapping pass writes it.
    /// A label only reads correctly next to every other intent's, which is why the map writes them all.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>Text sent on the user's behalf when that button is pressed; <c>null</c> until written beside <see cref="Label"/>.</summary>
    public string? DefaultValue { get; set; }

    /// <summary>Where this intent came from.</summary>
    public Provenance Origin { get; set; } = Provenance.Authored;
}

/// <summary>One domain agent under construction: its prose, its toolkit and the facts about its C# that <c>agents.json</c> does not carry.</summary>
public sealed class AgentDraft
{
    /// <summary>Agent ID, matching an intent name; <c>null</c> until the entry is opened against a named intent from the map.</summary>
    public string? ID { get; set; }

    /// <summary>
    /// What the client has said about the work this agent does, in their own words, as the interview has gathered it step by step.
    /// It holds what no section is addressed to (which screen they open, who rings, what goes wrong on a Saturday) and never enters the domain.
    /// </summary>
    public List<KnownFact> Known { get; set; } = [];

    /// <summary>What the agent is for, its scope and its boundaries; <c>null</c> until the AgentTarget pass settles it.</summary>
    public string? Target { get; set; }

    /// <summary>
    /// What falls to this agent, addressed to a colleague who might consult it, published on its A2A card and never in its own prompt.
    /// The AgentTerritory pass settles it once the toolkit stands; every agent carries one, since an unread one costs nothing.
    /// </summary>
    public string? Territory { get; set; }

    /// <summary>How the agent goes about its work; <c>null</c> until the AgentInstructions pass, which runs after the toolkit it speaks about.</summary>
    public string? Instructions { get; set; }

    /// <summary>How the agent presents its own tools' output; <c>null</c> until the AgentFormatting pass, the last before the client accepts the agent.</summary>
    public string? Formatting { get; set; }

    /// <summary>Tone and voice, optional in the framework; <c>null</c> until the AgentPersonality pass and legitimately so even once accepted.</summary>
    public string? Personality { get; set; }

    /// <summary>BCP 47 language code, such as <c>en-US</c>.</summary>
    public string Language { get; set; } = "en-US";

    /// <summary>Prompt version string, for tracking iterations of the prose.</summary>
    public string Version { get; set; } = "1";

    /// <summary>The agent's toolkit.</summary>
    public List<ToolDraft> Tools { get; set; } = [];

    /// <summary>
    /// AdditionalProperties entries, kept verbatim and written back as read, so a key Alembic has no use for survives a round trip.
    /// </summary>
    public List<Dictionary<string, object>> UnmodelledProperties { get; set; } = [];

    /// <summary>The facts about this agent's C# that <c>agents.json</c> does not carry.</summary>
    public AgentCodeFacts Code { get; set; } = new();

    /// <summary>Where this agent came from.</summary>
    public Provenance Origin { get; set; } = Provenance.Authored;
}

/// <summary>
/// One tool under construction: the contract between the agent's prose and a C# method.
/// </summary>
public sealed class ToolDraft
{
    /// <summary>Tool name, emitted as the C# method name the model calls; <c>null</c> until the AgentToolkit pass declares it.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// What the tool does, read by the model when it weighs whether to call it.
    /// The framework splices ToolDescriptionContextGuidance onto it when the tool declares a context-scoped parameter.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>The tool's parameters, in the order the C# method declares them.</summary>
    public List<ToolParameterDraft> Parameters { get; set; } = [];

    /// <summary>Where this tool came from.</summary>
    public Provenance Origin { get; set; } = Provenance.Authored;
}

/// <summary>
/// One tool parameter under construction.
/// </summary>
public sealed class ToolParameterDraft
{
    /// <summary>Parameter name, which must match the C# method's parameter name.</summary>
    public string? Name { get; set; }

    /// <summary>What the parameter is, which reaches the model's schema unchanged since the framework splices nothing onto it.</summary>
    public string? Description { get; set; }

    /// <summary>Whether the model must supply this parameter.</summary>
    public bool Required { get; set; } = true;

    /// <summary>
    /// <c>context</c> when resolved from the session's variables, <c>request</c> when obtained from the user, <c>null</c> for a value the model authors.
    /// It is inferred from one question about what the system knows of a user on arrival, never asked per parameter.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>Whether a context value is published to the conversation's <c>shared_context</c> for the other agents; meaningful only with scope <c>context</c>.</summary>
    public bool Shared { get; set; }
}

/// <summary>
/// What an agent's C# declares and <c>agents.json</c> does not: class names, namespace, tier, MCP servers and colleagues.
/// On import all of it is unknown, so Alembic proposes class names from the agent ID and flags them inferred, a proposal for the client to confirm.
/// </summary>
public sealed class AgentCodeFacts
{
    /// <summary>
    /// Namespace of the generated classes, left <c>null</c> on import rather than guessed.
    /// Nothing in agents.json implies it and a confident wrong value is worse than one the client will still be asked for.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>Proposes a class name from an intent name by the framework's convention, <c>billing</c> and <c>Agent</c> giving <c>BillingAgent</c>; <c>null</c> without a name.</summary>
    public static string? ProposeClassName(string? intentName, string suffix)
    {
        string bareName = intentName?.Trim() ?? string.Empty;

        // The intent capitalised and suffixed, as the example plugin names its agent and tool classes.
        return bareName.Length > 0 ? $"{char.ToUpperInvariant(bareName[0])}{bareName[1..]}{suffix}" : null;
    }

    /// <summary>Name of the <c>MorganaAgent</c> subclass, such as <c>BillingAgent</c>.</summary>
    public string? AgentClassName { get; set; }

    /// <summary>Name of the <c>MorganaTool</c> subclass, such as <c>BillingTool</c>; <c>null</c> for an MCP-only agent, which is legal.</summary>
    public string? ToolClassName { get; set; }

    /// <summary>The model tier the agent runs on, declared in C# through <c>[RequiresLLMTier]</c>.</summary>
    public Morgana.AI.Records.LLMTier? Tier { get; set; }

    /// <summary>MCP server declarations, one per <c>[UsesMCPServer]</c> attribute.</summary>
    public List<string> MCPServers { get; set; } = [];

    /// <summary>
    /// The colleagues this agent may put a question to, one per <c>[ConsultsAgent]</c>: an intent and the partner publishing it where that is not this domain.
    /// agents.json carries none of it, so the Collaboration form settles it over the whole domain; the framework refuses a second hop,
    /// so an edge is worth declaring only where the colleague answers out of its own books.
    /// </summary>
    public List<Morgana.AI.Records.PeerReference> Consults { get; set; } = [];

    /// <summary>Whether the values above were inferred by Alembic rather than stated by the client, as everything reconstructed at import is.</summary>
    public bool Inferred { get; set; }
}
