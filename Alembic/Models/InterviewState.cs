using Morgana.Contracts;

namespace Alembic.Models;

/// <summary>
/// Which pass of the interview is running.
/// </summary>
/// <remarks>The mapping pass runs once and every other pass runs once per intent it produced.</remarks>
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
    /// The workflows: the procedures whose steps the framework keeps in order, composed of the tools
    /// the toolkit has just settled.
    /// </summary>
    AgentWorkflows,

    /// <summary>
    /// What this agent is characteristically the one to be asked about: its Territory and nothing
    /// else. After the toolkit, because a territory an agent's own tools do not cover is a promise
    /// another agent would hold it to.
    /// </summary>
    AgentTerritory,

    /// <summary>
    /// How the agent goes about the work: its Instructions, now that there is a toolkit for them to speak about.
    /// </summary>
    AgentInstructions,

    /// <summary>
    /// How the agent lays out what its tools return: its Formatting and nothing else.
    /// </summary>
    AgentFormatting
}

/// <summary>
/// One interview in progress.
/// </summary>
/// <remarks>The state holds what is established and what may be written next while the model conducts the conversation.</remarks>
public sealed class InterviewState
{
    /// <summary>
    /// Which pass is running.
    /// </summary>
    public InterviewStep Pass { get; set; } = InterviewStep.DomainMapper;

    /// <summary>
    /// The question on the table: the last thing Alembic said to the client.
    /// </summary>
    /// <remarks>The transcript lives in the agent's session, so this holds only what the screen asks right now.</remarks>
    public string? Question { get; set; }

    /// <summary>
    /// The worked example standing in the answer box under the question on the table.
    /// </summary>
    /// <remarks>Cleared every exchange, so it never stands under the follow-up of the question it was written for.</remarks>
    public string? Example { get; set; }

    /// <summary>
    /// The prose just written for the agent, shown to the client word for word on the turn that asks
    /// whether it is right.
    /// </summary>
    /// <remarks>The words are drawn apart from the introducing sentence so the client approves the agent's prose and nothing else.</remarks>
    public string? Quoted { get; set; }

    /// <summary>
    /// What this step adds to the agent in hand, over the question that asks for it.
    /// </summary>
    /// <remarks>Written on the turn a step lands on and cleared every exchange, so a follow-up never claims the interview has moved on.</remarks>
    public string? Placing { get; set; }

    /// <summary>
    /// The button attached to the question on the table and whether it was pressed.
    /// </summary>
    /// <remarks>It is one button carrying the answer that adds nothing; the pressed flag is what shows the gesture happened.</remarks>
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
    // The sentence that places the step and the house example of the mapping pass belong to the opening question alone.
    public bool PassJustOpened => Exchanges <= PassOpenedAt + 1;

    /// <summary>
    /// The domain map: every kind of request this business gets, in the order they were named.
    /// </summary>
    /// <remarks>One entry becomes one intent and one agent; the agent passes run once down this list.</remarks>
    public List<IntentDraft> Map { get; } = [];

    /// <summary>
    /// Which entry of the map is being written, or -1 while the map itself is being drawn.
    /// </summary>
    public int At { get; set; } = -1;

    /// <summary>
    /// The intent being built: the map entry the interview stands on.
    /// </summary>
    /// <remarks>While <see cref="At"/> is -1 the map is still being drawn and a scratch intent stands in for the missing entry.</remarks>
    public IntentDraft Intent
    {
        // The entry at the cursor when there is one; the scratch intent while the map is drawn.
        get => At >= 0 && At < Map.Count ? Map[At] : field;
    } = new();

    /// <summary>
    /// Whether the interview is standing on one entry of the map or on the whole domain.
    /// </summary>
    /// <remarks>On the map what is found out belongs to the business, since the agent held there is never written.</remarks>
    public bool OnAnEntry => At >= 0 && At < Map.Count;

    /// <summary>
    /// The agent being built for that intent. A fresh one each time the interview moves down the map.
    /// </summary>
    public AgentDraft Agent { get; set; } = new();

    /// <summary>
    /// Set when the entry in hand is a section of an agent that already exists, opened from the walk.
    /// </summary>
    /// <remarks>Only stepping back from the first step and letting the agent in again read it.</remarks>
    public AgentRevision? Revision { get; set; }

    /// <summary>
    /// The interview set aside while one agent behind its frontier is corrected; <c>null</c> otherwise.
    /// </summary>
    public InterviewState? Resume { get; set; }

    /// <summary>
    /// Whether Alembic considers this pass's fields settled.
    /// </summary>
    /// <remarks>Set only through the <c>SetPassCompleted</c> tool, since a marker inside prose can be produced by accident.</remarks>
    public bool ReadyForReview { get; set; }

    /// <summary>
    /// The button Alembic attached to the question it is asking right now, cleared when answered.
    /// </summary>
    public QuickReply? PendingChoice { get; set; }

    /// <inheritdoc cref="Example" />
    public string? PendingExample { get; set; }

    /// <inheritdoc cref="Placing" />
    public string? PendingPlacing { get; set; }

    /// <inheritdoc cref="Quoted" />
    public string? PendingQuoted { get; set; }

    /// <summary>
    /// Words offered for the agent's voice and the ones Alembic is about to offer.
    /// </summary>
    /// <remarks>They are picked several at a time and so are shown as a cloud, since nobody has a ready sentence about how their staff should sound.</remarks>
    public IReadOnlyList<string> Traits { get; set; } = [];

    /// <inheritdoc cref="Traits" />
    public List<string> PendingTraits { get; } = [];

    /// <summary>
    /// Opens a turn: clears the error and every offer the tools of the last turn queued, so nothing stale rides into the new one.
    /// </summary>
    public void OpenTurn()
    {
        // A failure belongs to the turn that raised it and a healthy turn must not inherit it.
        Error = null;

        // Whatever was queued before is for a question already answered.
        PendingChoice = null;
        PendingExample = null;
        PendingPlacing = null;
        PendingQuoted = null;
        PendingTraits.Clear();

        // The rows highlighted last turn are recomputed from this turn's changes.
        Changed.Clear();
    }

    /// <summary>
    /// Puts the offers the turn's tools queued on screen, replacing the ones under the question that was just answered.
    /// </summary>
    public void ShowTurnOffers()
    {
        // Each offer on screen becomes what the tools queued this turn, so a turn that queued none clears it.
        Choice = PendingChoice;
        Example = PendingExample;
        Placing = PendingPlacing;
        Quoted = PendingQuoted;
        Traits = [.. PendingTraits];

        // A fresh button has not been pressed yet.
        Chosen = false;
    }

    /// <summary>
    /// The offered words that the agent's voice already carries, so a reopened step shows the voice that exists.
    /// </summary>
    /// <remarks>A word is lit only when the prose contains it literally as a whole word, never for a similar meaning.</remarks>
    public IReadOnlyList<string> TraitsTaken =>
        // A voice already written lights the offered words it contains; an unwritten voice lights none.
        Agent.Personality is { Length: > 0 } voice
            ? [.. Traits.Where(trait => Carries(voice, trait))]
            : [];

    /// <summary>
    /// Whether a piece of prose uses a word, as a word and not as part of a longer one.
    /// </summary>
    private static bool Carries(string prose, string trait)
    {
        // Every occurrence is checked, since the first may sit inside a longer word.
        for (int at = prose.IndexOf(trait, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = prose.IndexOf(trait, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            // The position just after the match, where the next letter would continue the word.
            int end = at + trait.Length;

            // Letters on either side make it part of a longer word, so 'formal' does not light inside 'informal'.
            if ((at == 0 || !char.IsLetter(prose[at - 1]))
                && (end == prose.Length || !char.IsLetter(prose[end])))
                return true;
        }

        // No occurrence stands as a whole word.
        return false;
    }

    /// <summary>
    /// Why the last exchange failed, when it did. Null on a healthy interview.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// The fields the last exchange actually moved.
    /// </summary>
    /// <remarks>Computed by diffing a snapshot across the exchange, since a tool called with the same value changed nothing.</remarks>
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
        ["agentTerritory"] = Agent.Territory,
        ["agentPersonality"] = Agent.Personality,
        ["agentInstructions"] = Agent.Instructions,
        ["agentFormatting"] = Agent.Formatting,

        // The whole toolkit as one string: any change to a tool, its parameters or its returns moves it and lights the toolkit panel.
        ["tools"] = string.Join("|", Agent.Tools.Select(t =>
            $"{t.Name}:{t.Description}:{string.Join(",", t.Parameters.Select(x => $"{x.Name}/{x.Scope}/{x.Required}/{x.Shared}/{x.Description}"))}:{string.Join(",", t.Returns.Select(r => $"{r.Name}/{r.Type}/{r.Description}"))}")),

        // The whole set of workflows as one string, lighting the workflows panel on any change.
        ["workflows"] = string.Join("|", Agent.Workflows.Select(w =>
            $"{w.Name}:{w.Description}:{string.Join(",", w.Steps.Select(x => $"{x.Name}/{string.Join("+", x.Tools)}"))}:{string.Join(",", w.Edges.Select(e => $"{e.Source}/{e.Tool}/{e.OnFailure}/{e.Target}/{string.Join("+", e.Carrying)}"))}"))
    };

    /// <summary>
    /// The fields the <em>running</em> pass is responsible for that are still unset.
    /// </summary>
    /// <remarks>The toolkit and workflows passes report only what is half-declared, since an agent with none of either is legal.</remarks>
    public IReadOnlyList<string> Missing() => Pass switch
    {
        // Each pass reports the fields it owns; an unknown pass reports none.
        InterviewStep.DomainMapper => MissingMap(),
        InterviewStep.AgentTarget => MissingTarget(),
        InterviewStep.AgentPersonality => MissingVoice(),
        InterviewStep.AgentToolkit => MissingToolkit(),
        InterviewStep.AgentWorkflows => MissingWorkflows(),
        InterviewStep.AgentTerritory => MissingTerritory(),
        InterviewStep.AgentInstructions => MissingInstructions(),
        InterviewStep.AgentFormatting => MissingFormatting(),
        _ => []
    };

    /// <summary>
    /// The map: at least one kind of request, each with its description and its whole button.
    /// </summary>
    private List<string> MissingMap()
    {
        // A map with no entry has named no kind of request at all.
        if (Map.Count == 0)
            return ["the domain map: not one kind of request has been named yet"];

        // The entries of the map that still lack something the client has to say.
        List<string> gaps = [];

        // Each entry is judged on its own fields.
        foreach (IntentDraft intent in Map)
        {
            // The gap names the entry in the client's words, with a placeholder while it has no name.
            string intentName = intent.Name ?? "(unnamed)";

            // Without a description the classifier has no route to this entry.
            if (string.IsNullOrWhiteSpace(intent.Description))
                gaps.Add($"what routes to '{intentName}'");

            // Without a label the user sees no button for this entry.
            if (string.IsNullOrWhiteSpace(intent.Label))
                gaps.Add($"the button for '{intentName}'");

            // Without a sentence the button cannot send anything.
            if (string.IsNullOrWhiteSpace(intent.DefaultValue))
                gaps.Add($"what pressing '{intentName}' sends");
        }

        // The gaps go to the model as what is still to be written.
        return gaps;
    }

    /// <summary>
    /// What the agent IS: its Target and nothing else at all.
    /// </summary>
    private List<string> MissingTarget()
    {
        List<string> gaps = [];

        // The pass owns the Target alone: the intent is the map's and the voice is the next pass's.
        if (string.IsNullOrWhiteSpace(Agent.Target))
            gaps.Add("agentTarget");

        // The gaps go to the model as what is still to be written.
        return gaps;
    }

    /// <summary>
    /// What the agent answers for, written for another agent to read.
    /// </summary>
    /// <remarks>Every agent owes one whether or not a colleague is declared, since its card is read by whoever holds a question it might settle.</remarks>
    private List<string> MissingTerritory() =>
        // A blank territory leaves the agent's card mute to every other agent.
        string.IsNullOrWhiteSpace(Agent.Territory) ? ["agentTerritory"] : [];

    /// <summary>
    /// How the agent sounds.
    /// </summary>
    private List<string> MissingVoice() =>
        // A blank voice leaves the agent without a tone.
        string.IsNullOrWhiteSpace(Agent.Personality) ? ["agentPersonality"] : [];

    /// <summary>
    /// Tools that were opened and never finished. A tool with no parameters is not one of them: a
    /// tool that takes nothing is ordinary. One that hands back nothing is, since startup refuses it.
    /// </summary>
    private List<string> MissingToolkit() =>
        // A tool without a description cannot be weighed by the model; one that hands back nothing is refused at startup.
        [.. Agent.Tools
                .Where(t => string.IsNullOrWhiteSpace(t.Description))
                .Select(t => $"description of {t.Name ?? "(unnamed tool)"}"),
            .. Agent.Tools
                .Where(t => !string.IsNullOrWhiteSpace(t.Name) && t.Returns.Count == 0)
                .Select(t => $"what {t.Name} hands back")];

    /// <summary>
    /// Workflows that were opened and never finished. An agent with none is legal, so emptiness is
    /// no gate: only a workflow without description or steps and a step without tools are reported.
    /// </summary>
    private List<string> MissingWorkflows() =>
        // Each report names a half-declared workflow part so the client can finish it.
        [.. Agent.Workflows
                .Where(w => string.IsNullOrWhiteSpace(w.Description))
                .Select(w => $"description of workflow {w.Name ?? "(unnamed workflow)"}"),
            .. Agent.Workflows
                .Where(w => w.Steps.Count == 0)
                .Select(w => $"steps of workflow {w.Name ?? "(unnamed workflow)"}"),
            .. Agent.Workflows.SelectMany(w => w.Steps
                .Where(x => x.Tools.Count == 0)
                .Select(x => $"tools of step {x.Name ?? "(unnamed step)"} of workflow {w.Name ?? "(unnamed workflow)"}"))];

    /// <summary>
    /// How the agent goes about the work, which could not be written before the toolkit existed.
    /// </summary>
    private List<string> MissingInstructions() =>
        // Blank Instructions leave the agent without rules for its tools.
        string.IsNullOrWhiteSpace(Agent.Instructions) ? ["agentInstructions"] : [];

    /// <summary>
    /// How the agent lays out what its tools return.
    /// </summary>
    private List<string> MissingFormatting() =>
        // Blank Formatting leaves the agent without a shape for its tools' output.
        string.IsNullOrWhiteSpace(Agent.Formatting) ? ["agentFormatting"] : [];
}
