using Morgana.Contracts;

namespace Alembic.Model;

/// <summary>
/// Which pass of the interview is running.
/// </summary>
/// <remarks>
/// The mapping pass runs once and the other three run once per intent it produced. That order is
/// forced twice over. The intents are what a classifier weighs against each other, so they are
/// designed together or they collide — an intent settled alone is a description nobody compared to
/// anything. And within an agent, Instructions and Formatting speak about its tools, so they cannot
/// be written before the toolkit exists.
/// </remarks>
public enum InterviewPass
{
    /// <summary>
    /// The domain map: every kind of request this business gets, named and described, before any
    /// agent is written. Runs once per interview and settles no agent at all.
    /// </summary>
    DomainMapper,

    /// <summary>
    /// What the agent is for, where it stops, how it sounds, and the intent that routes to it.
    /// Writes the intent's four fields plus the agent's Target and Personality — and deliberately
    /// not its Instructions or Formatting.
    /// </summary>
    AgentModeler,

    /// <summary>
    /// The toolkit: one tool at a time, its contract and its parameters.
    /// </summary>
    ToolkitModeler,

    /// <summary>
    /// Back to Instructions and Formatting, now that there is a toolkit for them to speak about.
    /// </summary>
    AgentFinalizer
}

/// <summary>
/// One interview in progress.
/// </summary>
/// <remarks>
/// The state machine lives here, in C#, and the conducting lives in the model. That split is the
/// architecture: what has been established, which pass is running and what may be written next are
/// facts, and facts do not belong to a language model's discretion. What the model owns is the
/// conversation — which question to ask next, and how to phrase a domain expert's answer as
/// dispositive prose.
/// </remarks>
public sealed class InterviewState
{
    /// <summary>
    /// Somewhere for a tool called before the map exists to write, so a stray call is a wasted call
    /// rather than a crash.
    /// </summary>
    private readonly IntentDraft nowhere = new();

    /// <summary>
    /// Which pass is running.
    /// </summary>
    public InterviewPass Pass { get; set; } = InterviewPass.DomainMapper;

    /// <summary>
    /// The question on the table: the last thing Alembic said to the client.
    /// </summary>
    /// <remarks>
    /// One question, not a log. The conversation is the agent's — it runs in a live
    /// <c>AgentSession</c> and is what the model reads back — and a second copy on this side would
    /// be a transcript nobody needs kept in step with one that already exists. What the screen
    /// needs is what is being asked right now.
    /// </remarks>
    public string? Question { get; set; }

    /// <summary>
    /// Buttons attached to the question on the table, and the one that was pressed.
    /// </summary>
    /// <remarks>
    /// The pressed id is held because a button's label and the answer it sends are deliberately
    /// different things, so a row that stays lit is the only sign the gesture happened at all.
    /// </remarks>
    public IReadOnlyList<QuickReply> Choices { get; set; } = [];

    /// <inheritdoc cref="Choices" />
    public string? ChosenId { get; set; }

    /// <summary>
    /// How many exchanges have happened. The caret follows it, and nothing else does.
    /// </summary>
    public int Exchanges { get; set; }

    /// <summary>
    /// The domain map: every kind of request this business gets, in the order they were named.
    /// </summary>
    /// <remarks>
    /// The interview's spine. One entry becomes one intent and one agent, and the three later passes
    /// run once down this list — which is why the map is settled first and whole: what routes to an
    /// agent is only correct relative to what routes to the others.
    /// </remarks>
    public List<IntentDraft> Map { get; } = [];

    /// <summary>
    /// Which entry of the map is being written, or -1 while the map itself is being drawn.
    /// </summary>
    public int At { get; set; } = -1;

    /// <summary>
    /// The intent being built: the map entry the interview stands on.
    /// </summary>
    public IntentDraft Intent => At >= 0 && At < Map.Count ? Map[At] : nowhere;

    /// <summary>
    /// The agent being built for that intent. A fresh one each time the interview moves down the map.
    /// </summary>
    public AgentDraft Agent { get; set; } = new();

    /// <summary>
    /// Whether Alembic considers this pass's fields settled. A statement about the configuration,
    /// never about the client's patience.
    /// </summary>
    /// <remarks>
    /// Set only through the <c>SetPassCompleted</c> tool, never by a token in Alembic's text —
    /// the same out-of-band rule Morgana applies to its own turn continuation, and for the same
    /// reason: a marker inside prose is a marker the prose can accidentally produce.
    /// </remarks>
    public bool ReadyForReview { get; set; }

    /// <summary>
    /// Buttons Alembic attached to the question it is asking right now, cleared when answered.
    /// </summary>
    public List<QuickReply> PendingChoices { get; } = [];

    /// <summary>
    /// Why the last exchange failed, when it did. Null on a healthy interview.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// The fields the last exchange actually moved.
    /// </summary>
    /// <remarks>
    /// The two-column layout exists so the client watches their domain being written, and that only
    /// works if the panel says what just changed. Without it the same rows sit there turn after turn
    /// and the panel reads as decoration — which is exactly what it becomes once a field is set and
    /// never mentioned again.
    /// <para>
    /// Computed by diffing a snapshot across the exchange rather than reported by the tools, because
    /// a tool called twice with the same value changed nothing and should not claim to have.
    /// </para>
    /// </remarks>
    public HashSet<string> Changed { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Every field this interview can write, by the key the panel highlights it under.
    /// </summary>
    public Dictionary<string, string?> Snapshot() => new(StringComparer.Ordinal)
    {
        ["intentName"] = Intent.Name,
        ["intentDescription"] = Intent.Description,
        ["intentLabel"] = Intent.Label,
        ["intentDefaultValue"] = Intent.DefaultValue,
        ["agentTarget"] = Agent.Target,
        ["agentPersonality"] = Agent.Personality,
        ["agentInstructions"] = Agent.Instructions,
        ["agentFormatting"] = Agent.Formatting,

        // The whole toolkit as one string: any tool added, dropped, renamed, redescribed or
        // reparametrised moves it, which is precisely when the toolkit panel deserves attention.
        ["tools"] = string.Join("|", Agent.Tools.Select(t =>
            $"{t.Name}:{t.Description}:{string.Join(",", t.Parameters.Select(x => $"{x.Name}/{x.Scope}/{x.Required}/{x.Shared}/{x.Description}"))}"))
    };

    /// <summary>
    /// The fields the <em>running</em> pass is responsible for that are still unset.
    /// </summary>
    /// <remarks>
    /// Pass-scoped on purpose, and it is what makes <c>SetPassCompleted</c> mean anything: a pass is
    /// complete when the fields it owns are set, never when the fields of a later one are still
    /// blank. The toolkit pass owns no field at all — an agent with no native tools is a legal
    /// configuration, the MCP-only case — so it reports only tools left half-declared, and its
    /// emptiness is a decision Alembic must have taken with the client rather than a gate.
    /// </remarks>
    public IReadOnlyList<string> Missing() => Pass switch
    {
        InterviewPass.DomainMapper => MissingMap(),
        InterviewPass.AgentModeler => MissingFunctional(),
        InterviewPass.ToolkitModeler => MissingToolkit(),
        InterviewPass.AgentFinalizer => MissingReturn(),
        _ => []
    };

    /// <summary>
    /// The map: at least one kind of request, each one complete — the whole <c>Intents</c> section
    /// of an <c>agents.json</c> and nothing less.
    /// </summary>
    /// <remarks>
    /// All four fields, because all four are read against their neighbours: the description by the
    /// classifier, the label and its sentence by a user seeing every button at once. An entry
    /// missing one of them is not half-written, it is a route that cannot be taken or a button that
    /// cannot be pressed.
    /// </remarks>
    private List<string> MissingMap()
    {
        if (Map.Count == 0)
            return ["the domain map: not one kind of request has been named yet"];

        List<string> missing = [];

        foreach (IntentDraft intent in Map)
        {
            string named = intent.Name ?? "(unnamed)";

            if (string.IsNullOrWhiteSpace(intent.Description)) missing.Add($"what routes to '{named}'");
            if (string.IsNullOrWhiteSpace(intent.Label)) missing.Add($"the button for '{named}'");
            if (string.IsNullOrWhiteSpace(intent.DefaultValue)) missing.Add($"what pressing '{named}' sends");
        }

        return missing;
    }

    /// <summary>
    /// What the agent IS: its Target and its Personality, and nothing about the intent.
    /// </summary>
    /// <remarks>
    /// The intent is the map's, settled whole and not reopened here. This pass owns the agent alone,
    /// which is why it has no tool that writes an intent — the two facts agree because they are the
    /// same fact stated in the two places that enforce it.
    /// </remarks>
    private List<string> MissingFunctional()
    {
        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(Agent.Target)) missing.Add("agentTarget");
        if (string.IsNullOrWhiteSpace(Agent.Personality)) missing.Add("agentPersonality");

        return missing;
    }

    /// <summary>
    /// Tools that were opened and never finished. A tool with no parameters is not one of them: a
    /// tool that takes nothing is ordinary.
    /// </summary>
    private List<string> MissingToolkit() =>
        [.. Agent.Tools
                .Where(t => string.IsNullOrWhiteSpace(t.Description))
                .Select(t => $"description of {t.Name ?? "(unnamed tool)"}")];

    /// <summary>
    /// The two sections that speak about the toolkit, and could not be written before it existed.
    /// </summary>
    private List<string> MissingReturn()
    {
        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(Agent.Instructions)) missing.Add("agentInstructions");
        if (string.IsNullOrWhiteSpace(Agent.Formatting)) missing.Add("agentFormatting");

        return missing;
    }
}
