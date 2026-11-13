using Morgana.Contracts;

namespace Alembic.Models;

/// <summary>
/// Which pass of the interview is running: the map once, then every agent pass once per entry of the map.
/// The intents are weighed against each other by the classifier, so they are designed together; within an agent,
/// Territory, Instructions and Formatting speak about its tools, so they come after the toolkit.
/// </summary>
public enum InterviewStep
{
    /// <summary>The domain map: every kind of request this business gets, named and described before any agent is written.</summary>
    DomainMapper,

    /// <summary>What the agent is for and where it stops: its Target and nothing else.</summary>
    AgentTarget,

    /// <summary>How the agent sounds: its Personality and nothing else.</summary>
    AgentPersonality,

    /// <summary>The toolkit: one tool at a time, its contract and its parameters.</summary>
    AgentToolkit,

    /// <summary>
    /// What this agent is characteristically the one to be asked about: its Territory and nothing else.
    /// It comes after the toolkit, since a territory the agent's own tools do not cover is a promise another agent would hold it to.
    /// </summary>
    AgentTerritory,

    /// <summary>How the agent goes about the work: its Instructions, now that there is a toolkit for them to speak about.</summary>
    AgentInstructions,

    /// <summary>How the agent lays out what its tools return: its Formatting and nothing else.</summary>
    AgentFormatting
}

/// <summary>
/// One interview in progress: the state machine, which the model conducts but never decides.
/// What has been established, which pass is running and what may be written next are facts; the model owns which question comes next and the prose.
/// </summary>
public sealed class InterviewState
{
    /// <summary>Which pass is running.</summary>
    public InterviewStep Pass { get; set; } = InterviewStep.DomainMapper;

    /// <summary>
    /// The question on the table: the last thing Alembic said to the client.
    /// One question and not a log, since the conversation lives in the pass's own session and the screen needs only what is asked now.
    /// </summary>
    public string? Question { get; set; }

    /// <summary>
    /// The worked example standing in the answer box under the question on the table.
    /// The model writes it after reading the map, so it comes from the client's own trade; it is cleared every exchange, belonging to one question.
    /// </summary>
    public string? Example { get; set; }

    /// <summary>
    /// The prose just written for the agent, shown to the client word for word on the turn that asks whether it is right.
    /// The client approves these exact words, so they are drawn apart from Alembic's sentence; cleared every exchange.
    /// </summary>
    public string? Quoted { get; set; }

    /// <summary>
    /// What this step adds to the agent in hand, drawn over the question in a quieter voice on the turn a step lands on.
    /// Cleared every exchange, so a follow-up carries none and the screen never says the interview moved on when it has not.
    /// </summary>
    public string? Placing { get; set; }

    /// <summary>
    /// The button attached to the question on the table, the one answer that agrees.
    /// Disagreement is a sentence typed in the box, which never closes; the button stays lit once pressed, the only sign the gesture happened.
    /// </summary>
    public QuickReply? Choice { get; set; }

    /// <inheritdoc cref="Choice" />
    public bool Chosen { get; set; }

    /// <summary>How many exchanges have happened over the whole interview, which the caret follows.</summary>
    public int Exchanges { get; set; }

    /// <summary>What <see cref="Exchanges"/> stood at when the pass now running opened.</summary>
    public int PassOpenedAt { get; set; }

    /// <summary>
    /// Whether the question on the table is the first one this pass has asked.
    /// The step's placing belongs to that question alone, as does the house example of the mapping pass.
    /// </summary>
    public bool PassJustOpened => Exchanges <= PassOpenedAt + 1;

    /// <summary>
    /// The domain map: every kind of request this business gets, in the order they were named.
    /// One entry becomes one intent and one agent, which the agent passes walk; it is settled first and whole, since routing is only right relative to the rest.
    /// </summary>
    public List<IntentDraft> Map { get; } = [];

    /// <summary>Which entry of the map is being written; -1 while the map itself is being drawn.</summary>
    public int At { get; set; } = -1;

    /// <summary>
    /// The intent being built: the map entry the interview stands on.
    /// While the map itself is drawn there is no entry, so a blank intent stands in rather than a null every tool would have to test.
    /// </summary>
    public IntentDraft Intent
    {
        get => At >= 0 && At < Map.Count ? Map[At] : field;
    } = new();

    /// <summary>
    /// Whether the interview is standing on one entry of the map, rather than on the whole domain.
    /// What is found out while the map is drawn belongs to the business: written onto the empty agent it would be lost with it.
    /// </summary>
    public bool OnAnEntry => At >= 0 && At < Map.Count;

    /// <summary>The agent being built for that intent, a fresh one each time the interview moves down the map.</summary>
    public AgentDraft Agent { get; set; } = new();

    /// <summary>
    /// Set when the agent in hand came out of the domain, opened from the walk, rather than out of a map being drawn today.
    /// The steps never read it, so correcting an agent is the same step as writing one; only the two ends do, since an edit has no map to step back onto
    /// and must put the agent back where it came from.
    /// </summary>
    public AgentRevision? Revision { get; set; }

    /// <summary>
    /// The interview set aside while one agent behind its frontier is corrected; <c>null</c> otherwise.
    /// </summary>
    public InterviewState? Resume { get; set; }

    /// <summary>
    /// Whether Alembic considers this pass's fields settled, a statement about the configuration and never about the client's patience.
    /// It is set only through the SetPassCompleted tool, never by a token in Alembic's text, which the prose could produce by accident.
    /// </summary>
    public bool ReadyForReview { get; set; }

    /// <summary>The button Alembic attached to the question it is asking right now, cleared when answered.</summary>
    public QuickReply? PendingChoice { get; set; }

    /// <inheritdoc cref="Example" />
    public string? PendingExample { get; set; }

    /// <inheritdoc cref="Placing" />
    public string? PendingPlacing { get; set; }

    /// <inheritdoc cref="Quoted" />
    public string? PendingQuoted { get; set; }

    /// <summary>
    /// Words offered for the agent's voice, picked several at a time, and the ones Alembic is about to offer.
    /// Nobody has a ready sentence about how their staff should sound, so the client recognises words instead of dictating them.
    /// </summary>
    public IReadOnlyList<string> Traits { get; set; } = [];

    /// <inheritdoc cref="Traits" />
    public List<string> PendingTraits { get; } = [];

    /// <summary>
    /// The offered words the agent's voice already carries, so the cloud opens showing the voice that exists.
    /// A word is lit only because the prose contains it, whole and ignoring case, never because it means something similar:
    /// the words are the client's to recognise, not Alembic's to put in their mouth.
    /// </summary>
    public IReadOnlyList<string> TraitsTaken =>
        Agent.Personality is { Length: > 0 } voice
            ? [.. Traits.Where(trait => Carries(voice, trait))]
            : [];

    /// <summary>
    /// Whether a piece of prose uses a word, as a word and not as part of a longer one.
    /// The boundary check keeps 'formal' from lighting inside 'informal', where a substring match would say the opposite of the truth.
    /// </summary>
    private static bool Carries(string prose, string trait)
    {
        // Every occurrence is tried, since the first may sit inside a longer word and a later one stand alone.
        for (int matchIndex = prose.IndexOf(trait, StringComparison.OrdinalIgnoreCase);
             matchIndex >= 0;
             matchIndex = prose.IndexOf(trait, matchIndex + 1, StringComparison.OrdinalIgnoreCase))
        {
            int afterIndex = matchIndex + trait.Length;

            // A letter on neither side means the word stands on its own.
            if ((matchIndex == 0 || !char.IsLetter(prose[matchIndex - 1]))
                && (afterIndex == prose.Length || !char.IsLetter(prose[afterIndex])))
                return true;
        }

        // Only inside longer words, or not at all.
        return false;
    }

    /// <summary>Why the last exchange failed; null on a healthy interview.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// The fields the last exchange actually moved, which the panel highlights so the client watches the domain being written.
    /// They come from a snapshot diffed across the exchange, since a tool called twice with one value changed nothing.
    /// </summary>
    public HashSet<string> Changed { get; } = new(StringComparer.Ordinal);

    /// <summary>Every field this interview can write, by the key the panel highlights it under.</summary>
    public Dictionary<string, string?> Snapshot() => new(StringComparer.Ordinal)
    {
        ["intentName"] = Intent.Name,
        ["intentDescription"] = Intent.Description,
        ["intentLabel"] = Intent.Label,
        ["intentDefaultValue"] = Intent.DefaultValue,
        ["agentTarget"] = Agent.Target,
        ["agentTerritory"] = Agent.Territory,
        ["agentPersonality"] = Agent.Personality,
        ["agentInstructions"] = Agent.Instructions,
        ["agentFormatting"] = Agent.Formatting,

        // The whole toolkit as one string: any tool added, dropped, renamed, redescribed or
        // reparametrised moves it, which is precisely when the toolkit panel deserves attention.
        ["tools"] = string.Join("|", Agent.Tools.Select(t =>
            $"{t.Name}:{t.Description}:{string.Join(",", t.Parameters.Select(x => $"{x.Name}/{x.Scope}/{x.Required}/{x.Shared}/{x.Description}"))}"))
    };

    /// <summary>
    /// The fields the running pass is responsible for that are still unset, which is what SetPassCompleted is held to.
    /// The toolkit pass owns no field, since an agent with no native tools is legal, so it reports only tools left half-declared.
    /// </summary>
    public IReadOnlyList<string> Missing() => Pass switch
    {
        // Each pass answers for its own fields alone; a pass not listed owns none.
        InterviewStep.DomainMapper => MissingMap(),
        InterviewStep.AgentTarget => MissingTarget(),
        InterviewStep.AgentPersonality => MissingVoice(),
        InterviewStep.AgentToolkit => MissingToolkit(),
        InterviewStep.AgentTerritory => MissingTerritory(),
        InterviewStep.AgentInstructions => MissingInstructions(),
        InterviewStep.AgentFormatting => MissingFormatting(),
        _ => []
    };

    /// <summary>
    /// The map: at least one kind of request, each with all four fields, the whole Intents section of an agents.json.
    /// An entry missing one is a route that cannot be taken or a button that cannot be pressed.
    /// </summary>
    private List<string> MissingMap()
    {
        // An empty map settles no domain at all.
        if (Map.Count == 0)
            return ["the domain map: not one kind of request has been named yet"];

        List<string> missing = [];

        // Every entry is checked, since the map is settled whole.
        foreach (IntentDraft intent in Map)
        {
            string intentName = intent.Name ?? "(unnamed)";

            // The classifier has nothing to route on.
            if (string.IsNullOrWhiteSpace(intent.Description))
                missing.Add($"what routes to '{intentName}'");

            // The user has no button to press.
            if (string.IsNullOrWhiteSpace(intent.Label))
                missing.Add($"the button for '{intentName}'");

            // The button would send nothing.
            if (string.IsNullOrWhiteSpace(intent.DefaultValue))
                missing.Add($"what pressing '{intentName}' sends");
        }

        // Each gap named the way the model can fix it.
        return missing;
    }

    /// <summary>
    /// What the agent is: its Target and nothing else, the one field this pass owns and the one its tools write.
    /// </summary>
    private List<string> MissingTarget()
    {
        List<string> missing = [];

        // The intent is the map's and the voice the next pass's, so only the Target can be missing here.
        if (string.IsNullOrWhiteSpace(Agent.Target))
            missing.Add("agentTarget");

        return missing;
    }

    /// <summary>
    /// What the agent answers for, written for another agent to read, which every agent owes whether or not anyone consults it.
    /// It is published on the agent's card, so an agent without one is mute to every caller and nothing inside the domain notices.
    /// </summary>
    private List<string> MissingTerritory() =>
        string.IsNullOrWhiteSpace(Agent.Territory) ? ["agentTerritory"] : [];

    /// <summary>How the agent sounds.</summary>
    private List<string> MissingVoice() =>
        string.IsNullOrWhiteSpace(Agent.Personality) ? ["agentPersonality"] : [];

    /// <summary>Tools opened and never described; a tool with no parameters is not one of them, since taking nothing is ordinary.</summary>
    private List<string> MissingToolkit() =>
        [.. Agent.Tools
                .Where(t => string.IsNullOrWhiteSpace(t.Description))
                .Select(t => $"description of {t.Name ?? "(unnamed tool)"}")];

    /// <summary>How the agent goes about the work, which could not be written before the toolkit existed.</summary>
    private List<string> MissingInstructions() =>
        string.IsNullOrWhiteSpace(Agent.Instructions) ? ["agentInstructions"] : [];

    /// <summary>How the agent lays out what its tools return.</summary>
    private List<string> MissingFormatting() =>
        string.IsNullOrWhiteSpace(Agent.Formatting) ? ["agentFormatting"] : [];
}
