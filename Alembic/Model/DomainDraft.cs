namespace Alembic.Model;

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
}

/// <summary>
/// One intent under construction: what the classifier matches on and what the presenter offers.
/// </summary>
public sealed class IntentDraft
{
    /// <summary>
    /// Intent name. Matches the agent ID and the <c>[HandlesIntent]</c> argument.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// What the intent covers, in the classifier's own words. This is the single most
    /// collision-prone field in a domain: two descriptions that overlap produce a classifier
    /// collision, which is why the cross-agent coherence pass exists.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Human-facing label, used for the presenter's quick reply buttons.
    /// </summary>
    public string? Label { get; set; }

    /// <summary>
    /// Text sent on the user's behalf when the presenter's button for this intent is pressed.
    /// </summary>
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
    public string? Target { get; set; }

    /// <summary>
    /// Domain-specific behavioural rules. Written after the toolkit, because they speak about it.
    /// </summary>
    public string? Instructions { get; set; }

    /// <summary>
    /// How the agent presents its own tools' output. Written after the toolkit, for the same reason.
    /// </summary>
    public string? Formatting { get; set; }

    /// <summary>
    /// Tone and voice. Optional in the framework, and optional here.
    /// </summary>
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
    /// Whether the values above were inferred by Alembic rather than stated by the client.
    /// True for everything reconstructed at import, since an uploaded <c>agents.json</c> carries
    /// none of it.
    /// </summary>
    public bool Inferred { get; set; }
}
