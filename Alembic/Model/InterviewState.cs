namespace Alembic.Model;

/// <summary>
/// Which pass of the interview is running.
/// </summary>
/// <remarks>
/// The passes are the states of the machine, and their order is forced by an inverse dependency:
/// an agent's Instructions and Formatting speak about its tools, so they cannot be written before
/// the toolkit exists. Hence functional first, toolkit second, and a return to the prose last.
/// </remarks>
public enum InterviewPass
{
    /// <summary>
    /// What the agent is for, where it stops, how it sounds, and the intent that routes to it.
    /// Writes the intent's four fields plus the agent's Target and Personality — and deliberately
    /// not its Instructions or Formatting.
    /// </summary>
    Functional,

    /// <summary>
    /// The toolkit: one tool at a time, its contract and its parameters.
    /// </summary>
    Toolkit,

    /// <summary>
    /// Back to Instructions and Formatting, now that there is a toolkit for them to speak about.
    /// </summary>
    Return
}

/// <summary>
/// Who said one line of the interview.
/// </summary>
public enum InterviewSpeaker
{
    /// <summary>Alembic.</summary>
    Alembic,

    /// <summary>The domain expert.</summary>
    Client
}

/// <summary>
/// One line of the interview.
/// </summary>
/// <param name="Speaker">Who said it.</param>
/// <param name="Text">What was said.</param>
public sealed record InterviewTurn(InterviewSpeaker Speaker, string Text);

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
    public InterviewPass Pass { get; set; } = InterviewPass.Functional;

    /// <summary>
    /// Everything said so far, oldest first.
    /// </summary>
    public List<InterviewTurn> Transcript { get; } = [];

    /// <summary>
    /// The intent being built.
    /// </summary>
    public IntentDraft Intent { get; } = new();

    /// <summary>
    /// The agent being built.
    /// </summary>
    public AgentDraft Agent { get; } = new();

    /// <summary>
    /// Whether Alembic considers this pass's fields settled. A statement about the configuration,
    /// never about the client's patience.
    /// </summary>
    public bool ReadyForReview { get; set; }

    /// <summary>
    /// Why the last exchange failed, when it did. Null on a healthy interview.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// The fields this pass is responsible for that are still unset — the same list Alembic is told
    /// about at the start of every turn.
    /// </summary>
    public IReadOnlyList<string> Missing()
    {
        List<string> missing = [];

        if (string.IsNullOrWhiteSpace(Intent.Name)) missing.Add("intentName");
        if (string.IsNullOrWhiteSpace(Intent.Description)) missing.Add("intentDescription");
        if (string.IsNullOrWhiteSpace(Intent.Label)) missing.Add("intentLabel");
        if (string.IsNullOrWhiteSpace(Intent.DefaultValue)) missing.Add("intentDefaultValue");
        if (string.IsNullOrWhiteSpace(Agent.Target)) missing.Add("agentTarget");
        if (string.IsNullOrWhiteSpace(Agent.Personality)) missing.Add("agentPersonality");

        return missing;
    }
}
