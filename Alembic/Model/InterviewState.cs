using Morgana.Contracts;

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
/// <param name="Choices">
/// Buttons Alembic attached to this question, if any. The contract is Morgana's own
/// <see cref="QuickReply"/> — the shape the client's own agents will emit — but the behaviour is
/// not a channel's: these never gate the text box, they sit beside it.
/// </param>
public sealed record InterviewTurn(
    InterviewSpeaker Speaker,
    string Text,
    IReadOnlyList<QuickReply>? Choices = null);

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
        InterviewPass.Functional => MissingFunctional(),
        InterviewPass.Toolkit => MissingToolkit(),
        InterviewPass.Return => MissingReturn(),
        _ => []
    };

    /// <summary>
    /// What the agent IS: the intent's four fields, plus its Target and Personality.
    /// </summary>
    private List<string> MissingFunctional()
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
