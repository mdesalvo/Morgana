using Morgana.Contracts;

namespace Distiller.Model;

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
public enum InterviewStep
{
    /// <summary>
    /// The domain map: every kind of request this business gets, named and described, before any
    /// agent is written. Runs once per interview and settles no agent at all.
    /// </summary>
    DomainMapper,

    /// <summary>
    /// What the agent is for and where it stops: its Target and nothing else.
    /// </summary>
    AgentTarget,

    /// <summary>
    /// How the agent sounds: its Personality and nothing else.
    /// </summary>
    AgentPersonality,

    /// <summary>
    /// The toolkit: one tool at a time, its contract and its parameters.
    /// </summary>
    AgentToolkit,

    /// <summary>
    /// How the agent goes about the work: its Instructions, now that there is a toolkit for them to speak about.
    /// </summary>
    AgentInstructions,

    /// <summary>
    /// How the agent lays out what its tools return: its Formatting and nothing else.
    /// </summary>
    AgentFormatting,

    /// <summary>
    /// The colleagues: which of the finished agents needs to be able to put a question to another
    /// one. Runs once, after the last entry of the map has its agent and settles no agent's own
    /// sections beyond the boundary sentence an edge contradicts.
    /// </summary>
    /// <remarks>
    /// Last for the same reason the map is first, read the other way round: an edge is a relation,
    /// and a relation cannot be asked about while half its ends are still unwritten. It sits outside
    /// the lap because it is asked once over the whole domain, never once per entry.
    /// </remarks>
    DomainColleagues
}

/// <summary>
/// One interview in progress.
/// </summary>
/// <remarks>
/// The state machine lives here, in C# and the conducting lives in the model. That split is the
/// architecture: what has been established, which pass is running and what may be written next are
/// facts and facts do not belong to a language model's discretion. What the model owns is the
/// conversation — which question to ask next and how to phrase a domain expert's answer as
/// dispositive prose.
/// </remarks>
public sealed class InterviewState
{
    /// <summary>
    /// Which pass is running.
    /// </summary>
    public InterviewStep Pass { get; set; } = InterviewStep.DomainMapper;

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
    /// The button attached to the question on the table and whether it was pressed.
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
    /// The interview's spine. One entry becomes one intent and one agent and the three later passes
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
    /// Set when the entry in hand came out of the domain — one section of an agent that already
    /// exists, opened from the walk — rather than out of a map being drawn today.
    /// </summary>
    /// <remarks>
    /// The steps themselves do not read this and that is what made the walk cheap to build: writing
    /// the Toolkit of an agent that already exists is the same step, asked the same way, as writing
    /// the Toolkit of one being written now. What reads it is the two ends — there is nothing behind
    /// the first step of an edit, since no map was drawn today to step back onto and letting the
    /// agent in again has to put it back where it came from rather than append it.
    /// </remarks>
    public AgentRevision? Revision { get; set; }

    /// <summary>
    /// Whether Alembic considers this pass's fields settled. A statement about the configuration,
    /// never about the client's patience.
    /// </summary>
    /// <remarks>
    /// Set only through the <c>SetPassCompleted</c> tool, never by a token in Alembic's text —
    /// the same out-of-band rule Morgana applies to its own turn continuation and for the same
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
    /// Words offered for the agent's voice and the ones Alembic is about to offer.
    /// </summary>
    /// <remarks>
    /// A cloud rather than buttons and the difference is not decoration: a button is one answer and
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
    /// The offered words the agent's voice already carries, so the cloud opens showing the voice
    /// that exists rather than an empty set.
    /// </summary>
    /// <remarks>
    /// A step reopened reads what is written as settled fact and this is that rule reaching the one
    /// part of the screen it had not. An agent whose Personality says it is formal and pragmatic,
    /// offered the word 'pragmatic' unlit, is being asked to say a second time what the question
    /// above it has just read back — so the client either re-picks what was already true, which is
    /// busywork, or leaves it dark and reads the cloud as a claim that it is not.
    /// <para>
    /// Literal, case-insensitive, on whole words: a word is lit because the prose contains it, never
    /// because it means something similar. Lighting 'precise' over a voice that says 'exacting' would
    /// be Alembic putting a word in the client's mouth on the one question whose whole point is that
    /// the words are theirs to recognise.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> TraitsTaken =>
        Agent.Personality is { Length: > 0 } voice
            ? [.. Traits.Where(trait => Carries(voice, trait))]
            : [];

    /// <summary>
    /// Whether a piece of prose uses a word, as a word and not as part of a longer one.
    /// </summary>
    // The boundary check is what keeps 'formal' from lighting up inside 'informal', which is the one
    // pair where a substring match would say the opposite of the truth.
    private static bool Carries(string prose, string trait)
    {
        for (int at = prose.IndexOf(trait, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = prose.IndexOf(trait, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            int after = at + trait.Length;

            if ((at == 0 || !char.IsLetter(prose[at - 1]))
                && (after == prose.Length || !char.IsLetter(prose[after])))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The colleagues declared by the closing step, none of them in the domain until the client
    /// agrees to the set.
    /// </summary>
    /// <remarks>
    /// Held here rather than written straight onto the agents for the reason the map is held here
    /// too: what the step settles is a set, judged whole and an edge dropped a turn after it was
    /// declared must leave no trace on an agent that is already in the client's configuration.
    /// </remarks>
    public List<ConsultationDraft> Colleagues { get; } = [];

    /// <summary>
    /// Why the last exchange failed, when it did. Null on a healthy interview.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// The fields the last exchange actually moved.
    /// </summary>
    /// <remarks>
    /// The two-column layout exists so the client watches their domain being written and that only
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
        ["agentConsultMeFor"] = Agent.ConsultMeFor,
        ["agentPersonality"] = Agent.Personality,
        ["agentInstructions"] = Agent.Instructions,
        ["agentFormatting"] = Agent.Formatting,

        // The whole toolkit as one string: any tool added, dropped, renamed, redescribed or
        // reparametrised moves it, which is precisely when the toolkit panel deserves attention.
        ["tools"] = string.Join("|", Agent.Tools.Select(t =>
            $"{t.Name}:{t.Description}:{string.Join(",", t.Parameters.Select(x => $"{x.Name}/{x.Scope}/{x.Required}/{x.Shared}/{x.Description}"))}")),

        // The whole set as one string, for the same reason as the toolkit above: an edge declared,
        // dropped or re-declared with different prose moves it and nothing finer is worth a row.
        ["colleagues"] = string.Join("|", Colleagues.Select(c =>
            $"{c.Asking}->{c.Asked}:{c.AskingInstructions}:{c.AskedInstructions}"))
    };

    /// <summary>
    /// The fields the <em>running</em> pass is responsible for that are still unset.
    /// </summary>
    /// <remarks>
    /// Pass-scoped on purpose and it is what makes <c>SetPassCompleted</c> mean anything: a pass is
    /// complete when the fields it owns are set, never when the fields of a later one are still
    /// blank. The toolkit pass owns no field at all — an agent with no native tools is a legal
    /// configuration, the MCP-only case — so it reports only tools left half-declared and its
    /// emptiness is a decision Alembic must have taken with the client rather than a gate.
    /// </remarks>
    public IReadOnlyList<string> Missing() => Pass switch
    {
        InterviewStep.DomainMapper => MissingMap(),
        InterviewStep.AgentTarget => MissingTarget(),
        InterviewStep.AgentPersonality => MissingVoice(),
        InterviewStep.AgentToolkit => MissingToolkit(),
        InterviewStep.AgentInstructions => MissingInstructions(),
        InterviewStep.AgentFormatting => MissingFormatting(),

        // Owns no field, like the toolkit pass and for the same kind of reason: a domain where no
        // agent needs a colleague is the ordinary domain, not an unfinished one, so an empty set is
        // a decision Alembic took with the client rather than a gate to hold the pass open.
        InterviewStep.DomainColleagues => [],
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

            if (string.IsNullOrWhiteSpace(intent.Description))
                missing.Add($"what routes to '{named}'");

            if (string.IsNullOrWhiteSpace(intent.Label))
                missing.Add($"the button for '{named}'");

            if (string.IsNullOrWhiteSpace(intent.DefaultValue))
                missing.Add($"what pressing '{named}' sends");
        }

        return missing;
    }

    /// <summary>
    /// What the agent IS: its Target and nothing else at all.
    /// </summary>
    /// <remarks>
    /// The intent is the map's, settled whole and not reopened here; the voice is the next pass's.
    /// This pass owns one field, which is why it has one tool that writes — the two facts agree
    /// because they are the same fact stated in the two places that enforce it.
    /// </remarks>
    private List<string> MissingTarget()
    {
        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(Agent.Target))
            missing.Add("agentTarget");

        // Settled by the same pass and drawn from the same scope, so it closes with it: an agent
        // whose Target stands and whose colleagues cannot read it is finished for itself and mute
        // to everyone else.
        if (string.IsNullOrWhiteSpace(Agent.ConsultMeFor))
            missing.Add("agentConsultMeFor");

        return missing;
    }

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
    private List<string> MissingInstructions() =>
        string.IsNullOrWhiteSpace(Agent.Instructions) ? ["agentInstructions"] : [];

    /// <summary>
    /// How the agent lays out what its tools return.
    /// </summary>
    private List<string> MissingFormatting() =>
        string.IsNullOrWhiteSpace(Agent.Formatting) ? ["agentFormatting"] : [];
}
