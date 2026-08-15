using Morgana.Contracts;

namespace Alembic.Model;

/// <summary>
/// Which pass of the interview is running.
/// </summary>
/// <remarks>
/// The mapping pass runs once and the other four run once per intent it produced. That order is
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
    /// What the agent is for and where it stops: its Target, and nothing else.
    /// </summary>
    AgentModeler,

    /// <summary>
    /// How the agent sounds: its Personality, and nothing else.
    /// </summary>
    /// <remarks>
    /// Its own pass rather than the second half of the one before, because what it asks for is a
    /// different kind of thing. A Target is dictated — the client knows what the work is — while a
    /// voice is recognised: they are shown what this one could sound like and say which of it is
    /// them. Joined to the Target, that recognition arrived as an afterthought at the end of a turn
    /// already spent on the work, and was answered 'yes, fine'.
    /// </remarks>
    AgentVoicer,

    /// <summary>
    /// The toolkit: one tool at a time, its contract and its parameters.
    /// </summary>
    ToolkitModeler,

    /// <summary>
    /// How the agent goes about the work: its Instructions, now that there is a toolkit for them to
    /// speak about.
    /// </summary>
    AgentInstructor,

    /// <summary>
    /// How the agent lays out what its tools return: its Formatting, and nothing else.
    /// </summary>
    /// <remarks>
    /// Its own step rather than the second question of the one before, for the reason every other
    /// step is its own: a step opens with a worked example of the answer it wants, and a step
    /// settling two sections can only carry the example for the first of them. The second arrived
    /// under an empty box, which is the screen this interview shows to nobody else.
    /// </remarks>
    AgentFormatter
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
    /// The worked example standing in the answer box under the question on the table.
    /// </summary>
    /// <remarks>
    /// Written by the model in the turn that opens a step, not held as a fixed string per pass: by
    /// then it has read the map, so the example can be an answer somebody in THEIR trade might have
    /// given, about the agent being built, rather than a stranger's business quoted at them while
    /// they are being asked about their own. Cleared every exchange, so it belongs to the question
    /// it was written for and never stands under the short follow-up that comes after it.
    /// </remarks>
    public string? Example { get; set; }

    /// <summary>
    /// The button attached to the question on the table, and whether it was pressed.
    /// </summary>
    /// <remarks>
    /// One button, never a row: the only answer worth a press is the one that agrees, since
    /// disagreement is a sentence saying what to change and the box it is typed into never closes.
    /// The pressed flag is held because a button's label and the answer it sends are deliberately
    /// different things, so a button that stays lit is the only sign the gesture happened at all.
    /// </remarks>
    public QuickReply? Choice { get; set; }

    /// <inheritdoc cref="Choice" />
    public bool Chosen { get; set; }

    /// <summary>
    /// How many exchanges have happened, over the whole interview. The caret follows it.
    /// </summary>
    public int Exchanges { get; set; }

    /// <summary>
    /// What <see cref="Exchanges"/> stood at when the pass now running opened.
    /// </summary>
    public int PassOpenedAt { get; set; }

    /// <summary>
    /// Whether the question on the table is the first one this pass has asked.
    /// </summary>
    // Read by the house example alone: it belongs to the opening question of the mapping pass and to
    // nothing after it. Every other example is the model's and clears itself with the turn it was
    // written for.
    public bool PassJustOpened => Exchanges <= PassOpenedAt + 1;

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
    /// <remarks>
    /// Backed by the C# 13 <c>field</c> keyword rather than a named field: while <see cref="At"/> is
    /// -1 (the map itself is still being drawn) there is no entry to index into, so the getter falls
    /// back to the auto-property's own backing store — initialised once below to a fresh
    /// <see cref="IntentDraft"/> — instead of throwing or returning <c>null</c>.
    /// </remarks>
    public IntentDraft Intent
    {
        get => At >= 0 && At < Map.Count ? Map[At] : field;
    } = new();

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
    /// The button Alembic attached to the question it is asking right now, cleared when answered.
    /// </summary>
    public QuickReply? PendingChoice { get; set; }

    /// <inheritdoc cref="Example" />
    public string? PendingExample { get; set; }

    /// <summary>
    /// Words offered for the agent's voice, and the ones Alembic is about to offer.
    /// </summary>
    /// <remarks>
    /// A cloud rather than buttons, and the difference is not decoration: a button is one answer and
    /// these are picked several at a time, so the shape has to say 'take what fits' rather than
    /// 'choose one'. It is the one question of the interview the client cannot dictate — nobody has
    /// a ready sentence about how their own staff should sound — and recognition is what they can do
    /// instead. The words are the model's, written for this domain and this agent after reading
    /// Morgana's own voice, so none of them can contradict her.
    /// </remarks>
    public IReadOnlyList<string> Traits { get; set; } = [];

    /// <inheritdoc cref="Traits" />
    public List<string> PendingTraits { get; } = [];

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
        InterviewPass.AgentModeler => MissingTarget(),
        InterviewPass.AgentVoicer => MissingVoice(),
        InterviewPass.ToolkitModeler => MissingToolkit(),
        InterviewPass.AgentInstructor => MissingInstructions(),
        InterviewPass.AgentFormatter => MissingFormatting(),
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
    /// What the agent IS: its Target, and nothing else at all.
    /// </summary>
    /// <remarks>
    /// The intent is the map's, settled whole and not reopened here; the voice is the next pass's.
    /// This pass owns one field, which is why it has one tool that writes — the two facts agree
    /// because they are the same fact stated in the two places that enforce it.
    /// </remarks>
    private List<string> MissingTarget() =>
        string.IsNullOrWhiteSpace(Agent.Target) ? ["agentTarget"] : [];

    /// <summary>
    /// How the agent sounds.
    /// </summary>
    private List<string> MissingVoice() =>
        string.IsNullOrWhiteSpace(Agent.Personality) ? ["agentPersonality"] : [];

    /// <summary>
    /// Tools that were opened and never finished. A tool with no parameters is not one of them: a
    /// tool that takes nothing is ordinary.
    /// </summary>
    private List<string> MissingToolkit() =>
        [.. Agent.Tools
                .Where(t => string.IsNullOrWhiteSpace(t.Description))
                .Select(t => $"description of {t.Name ?? "(unnamed tool)"}")];

    /// <summary>
    /// How the agent goes about the work, which could not be written before the toolkit existed.
    /// </summary>
    private List<string> MissingInstructions()
    {
        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(Agent.Instructions)) missing.Add("agentInstructions");

        return missing;
    }

    /// <summary>
    /// How the agent lays out what its tools return.
    /// </summary>
    private List<string> MissingFormatting() =>
        string.IsNullOrWhiteSpace(Agent.Formatting) ? ["agentFormatting"] : [];
}
