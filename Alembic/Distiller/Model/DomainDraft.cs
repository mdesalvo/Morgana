namespace Distiller.Model;

/// <summary>
/// The whole Morgana domain under construction: the single artifact the interview fills, the
/// validator checks, the recap composes and the emit reads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a Draft rather than the <c>Records</c> types directly.</b> The <c>Records</c> types are
/// the <em>serialization</em> model: immutable, complete, positional. The Draft is the
/// <em>editing</em> model, and an interview in progress is by definition incomplete — a tool whose
/// description has not been asked for yet is a different state from a tool whose description is
/// deliberately empty, and only a nullable field can tell the two apart. Every nullable string
/// below means precisely "not asked yet". Nothing here duplicates a concept the framework already
/// models: where a shape is final it IS the framework's own record.
/// </para>
/// <para>
/// <b>What survives that Alembic does not understand.</b> An uploaded <c>agents.json</c> may carry
/// AdditionalProperties entries beyond <c>Tools</c>. They are kept verbatim in
/// <see cref="AgentDraft.UnmodelledProperties"/> and written back untouched: the round-trip
/// invariant must not depend on Alembic having a use for every key it meets.
/// </para>
/// </remarks>
public sealed class DomainDraft
{
    /// <summary>
    /// Intent definitions, one per agent plus the framework's own <c>other</c>.
    /// </summary>
    public List<IntentDraft> Intents { get; set; } = [];

    /// <summary>
    /// Agent prompts, each keyed by an ID matching an intent name.
    /// </summary>
    public List<AgentDraft> Agents { get; set; } = [];

    /// <summary>
    /// Name of the uploaded file this Draft was imported from, or <c>null</c> for a greenfield
    /// Draft. Carried for the migration report, which has to name what it is diffing against.
    /// </summary>
    public string? ImportedFrom { get; set; }

    /// <summary>
    /// When this Draft was created or last imported into, UTC.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// The domain exactly as it was uploaded, frozen at import. <c>null</c> for a greenfield Draft.
    /// </summary>
    /// <remarks>
    /// The migration report has to diff against something, and <see cref="Provenance"/> alone cannot
    /// serve: it says an element was revised, never what it used to be, and "the parameter list
    /// changed" is only useful next to the list it changed from. Kept as a Draft rather than as the
    /// uploaded bytes so the diff compares like with like — two Drafts, one projection, no chance of
    /// reporting a difference that is really a parsing artefact.
    /// <para>
    /// Serialized with the rest, because an interview over a real domain does not fit in one
    /// sitting and a report that forgets its baseline on resume is a report that lies the second
    /// day. Its own baseline is always <c>null</c>: one level, never a chain.
    /// </para>
    /// </remarks>
    public DomainDraft? Baseline { get; set; }

    /// <summary>
    /// The interview as it stood when this file was written, or <c>null</c> when none was under way.
    /// </summary>
    /// <remarks>
    /// Without this the save file holds only what has been let into the domain, so a client who
    /// closes the tab while an agent is half written loses that agent and the map they dictated to
    /// get to it — which is precisely the moment somebody has to stop, since the accepted ones are
    /// the ones already behind them.
    /// <para>
    /// What is saved is the configuration in hand, never the conversation: the map, which entry is
    /// being written, which step of it, and what has been written so far. Resuming re-enters that
    /// step the way stepping back into it does — a fresh agent and a fresh session reading what is
    /// there as settled fact — because the memory of this interview has always been the
    /// configuration rather than the transcript.
    /// </para>
    /// </remarks>
    public InterviewSitting? Sitting { get; set; }

    /// <summary>
    /// The name the classifier falls back to when it cannot place a message at all.
    /// </summary>
    public const string FallbackIntent = "other";

    /// <summary>
    /// Puts the fallback intent in the domain if it is not already there.
    /// </summary>
    /// <remarks>
    /// The one intent no interview writes and no client edits. Every domain has it, it takes no
    /// agent — <c>HandlesIntentAgentRegistryService</c> exempts this single name from its
    /// bidirectional check — and it carries no label, because it is not a button anybody presses.
    /// A domain without it has nowhere to put the messages it does not cover, which is why the
    /// description is the framework's own words rather than something to phrase per client.
    /// <para>
    /// Matched case-insensitively but never rewritten: a domain that arrived with its own wording
    /// for the fallback keeps it, since that is the client's sentence about their own catch-all.
    /// </para>
    /// </remarks>
    public void EnsureFallbackIntent()
    {
        if (Intents.Any(i => string.Equals(i.Name, FallbackIntent, StringComparison.OrdinalIgnoreCase)))
            return;

        Intents.Add(new IntentDraft
        {
            Name = FallbackIntent,
            Description = "any other topic not expressly intercepted by known intents",
            Origin = Provenance.Authored
        });
    }
}

/// <summary>
/// An interview interrupted: where it had got to, and what it had in hand.
/// </summary>
/// <remarks>
/// Everything here is a fact the state machine owns, which is why it is exactly what has to survive
/// a closed tab. The entries already accepted are not here — they are in the domain itself, which is
/// where they belong the moment the client lets them in.
/// </remarks>
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
    /// The map as dictated, entries already accepted included: it is one list and the interview walks
    /// it, so half of it would resume as a different domain.
    /// </summary>
    public List<IntentDraft> Map { get; set; } = [];

    /// <summary>
    /// The agent in hand, however far it had got.
    /// </summary>
    public AgentDraft Agent { get; set; } = new();

    /// <summary>
    /// The worked example standing in the answer box, if the question it belongs to still is.
    /// </summary>
    /// <remarks>
    /// Carried only as a fallback for the turn that reopens this step: if that turn's own exchange
    /// writes a fresh one, the fresh one wins, exactly as it would have without a save in between.
    /// </remarks>
    public string? Example { get; set; }

    /// <summary>
    /// What the agent in hand was before it left the domain, when this sitting is one section of an
    /// agent being revised rather than a fresh entry being written.
    /// </summary>
    public AgentRevision? Revision { get; set; }

    /// <summary>
    /// The colleagues declared so far by the closing step, none of which is in the domain yet.
    /// </summary>
    /// <remarks>
    /// They are settled together and committed together — an edge is only correct next to the other
    /// edges, the way a map entry is only correct next to the other entries — so between the first
    /// declaration and the client's agreement they live nowhere but here, and a tab closed in
    /// between must not lose them.
    /// </remarks>
    public List<ConsultationDraft> Colleagues { get; set; } = [];
}

/// <summary>
/// One agent's licence to put a question to another: the edge, and the boundary prose that changes
/// with it.
/// </summary>
/// <remarks>
/// The prose travels with the edge because without it the edge is a defect. An agent whose
/// Instructions say a subject belongs to another bench and stops there is being told, in the same
/// prompt, that it may ask and that it may not — and the imperative sentence wins. So the tool that
/// declares an edge takes the asking agent's reconciled Instructions in the same call, and the two
/// land in the domain together or not at all.
/// </remarks>
public sealed class ConsultationDraft
{
    /// <summary>The intent of the agent that gains the <c>[ConsultsAgent]</c> declaration.</summary>
    public string Asking { get; set; } = string.Empty;

    /// <summary>The intent of the colleague it may put a question to.</summary>
    public string Asked { get; set; } = string.Empty;

    /// <summary>The asking agent's Instructions, rewritten so its own boundary admits the colleague.</summary>
    public string AskingInstructions { get; set; } = string.Empty;

    /// <summary>
    /// The colleague's Instructions, rewritten only where its own prose would have it refuse what it
    /// is now being asked for. <c>null</c> whenever they stand as they are, which is the ordinary
    /// case: answering a colleague is a turn the framework already governs, and a refusal to say
    /// something to a *user* is not one to reopen here.
    /// </summary>
    public string? AskedInstructions { get; set; }
}

/// <summary>
/// An agent taken out of the domain to be worked on, and what it needs to go back the way it came.
/// </summary>
/// <remarks>
/// An agent being edited must not also be sitting in the configuration, or letting it back in would
/// write it twice — the rule <c>BackAsync</c> already obeys when it steps out of one entry into the
/// one before. So the domain is without it for the length of the edit, and this is what puts it back:
/// its place in the two lists, since the order is the client's and an agent that walks to the bottom
/// every time somebody fixes a sentence turns that order into a history of edits; and the provenance
/// it arrived with, since an imported agent has to come back <see cref="Provenance.Revised"/> rather
/// than as something Alembic claims to have authored.
/// <para>
/// It exists only while a section is actually being rewritten. Reading an agent on the way past
/// creates none: leafing through a domain is not editing it, and an agent nobody has changed never
/// leaves the configuration at all.
/// </para>
/// </remarks>
public sealed class AgentRevision
{
    /// <summary>Where the intent stood in the domain's own list.</summary>
    public int IntentAt { get; set; } = -1;

    /// <summary>Where the agent stood in the domain's own list.</summary>
    public int AgentAt { get; set; } = -1;

    /// <summary>What the agent's provenance was before the edit opened.</summary>
    public Provenance Origin { get; set; }

    /// <summary>
    /// What the agent read when it left the domain, so leaving the edit can tell a rewrite from a
    /// look.
    /// </summary>
    /// <remarks>
    /// Opening a section is not changing it, and an agent that goes back exactly as it came must go
    /// back <see cref="Provenance.Imported"/> — otherwise the migration report names it among the
    /// things this sitting touched, and a report that lists everything the client opened is a report
    /// they stop reading.
    /// </remarks>
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
    /// <remarks><c>null</c> until asked, per the Draft-wide nullable convention — see the class
    /// remarks on <see cref="DomainDraft"/>.</remarks>
    public string? Description { get; set; }

    /// <summary>
    /// Human-facing label, used for the presenter's quick reply buttons.
    /// </summary>
    /// <remarks>
    /// <c>null</c> until the mapping pass writes the whole button set in one pass, once the intent
    /// list is closed — never written one intent at a time, because a label only reads correctly
    /// next to the labels of every other intent on the map.
    /// </remarks>
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
/// One domain agent under construction: its prose, its toolkit, and the facts about its C#
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
    /// Prompt type category. <c>"INTENT"</c> for a domain agent.
    /// </summary>
    public string Type { get; set; } = "INTENT";

    /// <summary>
    /// Prompt subtype. <c>"AGENT"</c> for a domain agent.
    /// </summary>
    public string SubType { get; set; } = "AGENT";

    /// <summary>
    /// What the agent is for: its scope and its boundaries.
    /// </summary>
    /// <remarks><c>null</c> until the <c>AgentTarget</c> pass settles it — the field
    /// <see cref="InterviewState.MissingTarget"/> tests to decide whether that pass may close.</remarks>
    public string? Target { get; set; }

    /// <summary>
    /// Domain-specific behavioural rules. Written after the toolkit, because they speak about it.
    /// </summary>
    /// <remarks><c>null</c> until the <c>AgentInstructions</c> pass runs, which is why the pass order
    /// puts the toolkit before it — instructions about tools cannot be asked for before the tools
    /// they describe exist.</remarks>
    public string? Instructions { get; set; }

    /// <summary>
    /// How the agent presents its own tools' output. Written after the toolkit, for the same reason.
    /// </summary>
    /// <remarks><c>null</c> until the <c>AgentFormatting</c> pass, the last one an entry goes
    /// through before <see cref="AcceptAsync"/>-style acceptance.</remarks>
    public string? Formatting { get; set; }

    /// <summary>
    /// Tone and voice. Optional in the framework, and optional here.
    /// </summary>
    /// <remarks><c>null</c> until the <c>AgentPersonality</c> pass; unlike <see cref="Target"/> etc. this
    /// one may legitimately stay <c>null</c> even once accepted, since a voice is optional.</remarks>
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
    /// AdditionalProperties entries other than <c>Tools</c>, kept verbatim so a key Alembic has no
    /// use for still survives a round trip. Values are <c>JsonElement</c>s and are written back as
    /// they were read.
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
/// One tool under construction: the contract between the agent's prose and a C# method.
/// </summary>
public sealed class ToolDraft
{
    /// <summary>
    /// Tool name. Must match the C# method name exactly — <c>MorganaToolAdapter.AddTool</c>
    /// validates the pair and fails startup on a mismatch.
    /// </summary>
    /// <remarks><c>null</c> until the <c>AgentToolkit</c> pass declares this tool via
    /// <c>DeclareTool</c>.</remarks>
    public string? Name { get; set; }

    /// <summary>
    /// What the tool does, read by the model when it weighs whether to call it. The framework
    /// splices <c>ToolDescriptionContextGuidance</c> onto this description when the tool declares
    /// at least one context-scoped parameter — so what is authored here is only half of what the
    /// model finally reads.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// The tool's parameters, in the order the C# method declares them.
    /// </summary>
    public List<ToolParameterDraft> Parameters { get; set; } = [];

    /// <summary>
    /// Where this tool came from.
    /// </summary>
    public Provenance Origin { get; set; } = Provenance.Authored;
}

/// <summary>
/// One tool parameter under construction.
/// </summary>
public sealed class ToolParameterDraft
{
    /// <summary>
    /// Parameter name. Must match the C# method's parameter name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// What the parameter is, exactly as authored: the framework splices no template onto a
    /// parameter description, so this string reaches the model's schema unchanged.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Whether the model must supply this parameter.
    /// </summary>
    public bool Required { get; set; } = true;

    /// <summary>
    /// <c>"context"</c> (resolved from the session's context variables) or <c>"request"</c>
    /// (obtained from the user). A parameter carrying a value the model itself authors declares
    /// no scope at all.
    /// </summary>
    /// <remarks>
    /// Never asked per parameter — inferred once from a single question about the client's setup
    /// (what the system already knows about a user the moment they arrive), then applied to every
    /// parameter of the toolkit: everything on that answer is <c>"context"</c>, everything else
    /// <c>"request"</c>. A bare <c>null</c> here is a third, legitimate value, not an unanswered
    /// question.
    /// </remarks>
    public string? Scope { get; set; }

    /// <summary>
    /// Whether the resolved value is published to the conversation-scoped <c>shared_context</c>
    /// registry so other agents can hydrate from it. Only meaningful when <see cref="Scope"/> is
    /// <c>"context"</c>.
    /// </summary>
    public bool Shared { get; set; }
}

/// <summary>
/// What an agent's C# declares and <c>agents.json</c> does not: class names, namespace, tier,
/// MCP servers.
/// </summary>
/// <remarks>
/// On import every field here is unknown, because the uploaded file simply does not contain them.
/// Alembic infers defaults from the agent ID by the framework's own naming convention and flags
/// them as such — an inferred value is a proposal for the client to confirm, never a finding.
/// </remarks>
public sealed class AgentCodeFacts
{
    /// <summary>
    /// Namespace of the generated classes.
    /// </summary>
    /// <remarks>
    /// Left <c>null</c> on import rather than guessed, unlike <see cref="AgentClassName"/> and
    /// <see cref="ToolClassName"/>: a namespace follows from nothing in <c>agents.json</c>, and a
    /// confident wrong value here is worse than one the interview will still ask about.
    /// </remarks>
    public string? Namespace { get; set; }

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
    /// The die the agent runs on, declared in C# via <c>[RequiresLLMTier]</c>.
    /// </summary>
    public Morgana.AI.Records.LLMTier? Tier { get; set; }

    /// <summary>
    /// MCP server declarations, one per <c>[UsesMCPServer]</c> attribute.
    /// </summary>
    public List<string> MCPServers { get; set; } = [];

    /// <summary>
    /// The colleagues this agent may put a question to, one intent name per
    /// <c>[ConsultsAgent]</c> attribute.
    /// </summary>
    /// <remarks>
    /// A C# fact like <see cref="MCPServers"/> — <c>agents.json</c> carries no trace of it, so an
    /// imported domain arrives with none and the colleagues step is where they are settled. It is
    /// nonetheless a domain question and not an infrastructural one: whether the accounts desk has
    /// to ring the greenhouse is something only the client knows about their own work, which is why
    /// it is asked in the interview rather than ticked on the emit page beside the tier.
    /// <para>
    /// The framework refuses a second hop — an agent answering a colleague finds its own peer
    /// functions refused — so an edge is worth declaring only where the colleague can answer out of
    /// its own books.
    /// </para>
    /// </remarks>
    public List<string> Consults { get; set; } = [];

    /// <summary>
    /// Whether the values above were inferred by Alembic rather than stated by the client.
    /// True for everything reconstructed at import, since an uploaded <c>agents.json</c> carries
    /// none of it.
    /// </summary>
    public bool Inferred { get; set; }
}
