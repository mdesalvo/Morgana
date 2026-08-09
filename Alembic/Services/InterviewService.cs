using System.Text.Json;
using System.Text.Json.Serialization;
using Alembic.Interfaces;
using Alembic.Model;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IInterviewService"/>: the C# state machine, driven turn by turn by the model.
/// </summary>
public class InterviewService : IInterviewService
{
    /// <summary>
    /// The prompt Alembic conducts the functional pass with.
    /// </summary>
    private const string FunctionalPassPromptId = "FunctionalPass";

    /// <summary>
    /// What the model is sent when the transcript is still empty. Never shown to the client: it
    /// exists because a chat completion needs a user turn to answer, not because anybody said it.
    /// </summary>
    private const string BootstrapMessage = "Begin the interview.";

    /// <summary>Section label the domain layer's Target carries, matching the framework layer's.</summary>
    private const string TargetMarker = "[TARGET]";

    /// <summary>Section label the domain layer's Personality carries, matching the framework layer's.</summary>
    private const string PersonalityMarker = "[PERSONALITY]";

    /// <summary>
    /// Lenient on purpose: the model is instructed to answer in strict JSON, and a rejected reply
    /// costs the client a turn of their own interview.
    /// </summary>
    private static readonly JsonSerializerOptions ResponseOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IAlembicPromptService alembicPromptService;
    private readonly ILLMService llmService;
    private readonly IDraftStateService draftStateService;
    private readonly ILogger logger;

    /// <summary>
    /// What the model has seen, kept apart from <see cref="InterviewState.Transcript"/>: this list
    /// carries the model's own replies as the raw JSON it emitted, which keeps the output contract
    /// in front of it, while the transcript carries only the sentences a human said or was told.
    /// </summary>
    private readonly List<ChatMessage> conversation = [];

    /// <inheritdoc />
    public InterviewState? Current { get; private set; }

    /// <summary>
    /// Initializes the interview service.
    /// </summary>
    /// <param name="alembicPromptService">Alembic's own conducting prose.</param>
    /// <param name="llmService">Provider abstraction; the interview runs on the Performance tier.</param>
    /// <param name="draftStateService">Where a committed interview lands.</param>
    /// <param name="logger">Logger for model-contract failures.</param>
    public InterviewService(
        IAlembicPromptService alembicPromptService,
        ILLMService llmService,
        IDraftStateService draftStateService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.llmService = llmService;
        this.draftStateService = draftStateService;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<InterviewState> StartAsync(CancellationToken cancellationToken = default)
    {
        Current = new InterviewState();
        conversation.Clear();
        conversation.Add(new ChatMessage(ChatRole.User, BootstrapMessage));

        await ExchangeAsync(Current, cancellationToken);
        return Current;
    }

    /// <inheritdoc />
    public async Task<InterviewState> AnswerAsync(string answer, CancellationToken cancellationToken = default)
    {
        InterviewState state = Current ?? await StartAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(answer))
            return state;

        state.Transcript.Add(new InterviewTurn(InterviewSpeaker.Client, answer));
        conversation.Add(new ChatMessage(ChatRole.User, answer));

        await ExchangeAsync(state, cancellationToken);
        return state;
    }

    /// <inheritdoc />
    public void Commit()
    {
        if (Current is not { } state)
            return;

        DomainDraft draft = draftStateService.Current ?? new DomainDraft();

        state.Intent.Origin = Provenance.Authored;
        state.Agent.Origin = Provenance.Authored;
        state.Agent.ID = state.Intent.Name;
        state.Agent.Code.Inferred = true;
        state.Agent.Code.AgentClassName = ProposeClassName(state.Intent.Name, "Agent");

        draft.Intents.Add(state.Intent);
        draft.Agents.Add(state.Agent);

        Current = null;
        conversation.Clear();

        // Re-setting the same instance is what raises Changed, so a page showing the Draft
        // re-renders whether or not the Draft object itself was new.
        draftStateService.Set(draft);
    }

    /// <inheritdoc />
    public void Abandon()
    {
        Current = null;
        conversation.Clear();
    }

    /// <summary>
    /// One round trip: send the system prompt plus everything said, read back the model's reply,
    /// merge whatever it proposed.
    /// </summary>
    private async Task ExchangeAsync(InterviewState state, CancellationToken cancellationToken)
    {
        state.Error = null;

        // Performance, directly rather than through CompleteWithSystemPromptAsync, which always
        // runs on the cheapest configured tier. Writing non-contradictory dispositive prose is the
        // exact task the Efficiency die is weakest at.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Performance);

        // The system message is rebuilt every turn so the state block below it is current — the
        // same reason the framework re-injects its held-context declaration per turn rather than
        // baking it into the prompt once.
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, $"{alembicPromptService.Compose(FunctionalPassPromptId)}\n\n{DescribeState(state)}"),
            .. conversation
        ];

        string raw;

        try
        {
            ChatResponse response = await chatClient.GetResponseAsync(messages, new ChatOptions(), cancellationToken);
            raw = response.Text;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The interview turn failed at the provider");
            state.Error = $"The model could not be reached: {ex.Message}";
            return;
        }

        FunctionalPassReply? reply = Parse(raw);

        if (reply is null)
        {
            logger.LogWarning("The model did not answer with the agreed JSON object: {Raw}", raw);
            state.Error = "The model answered outside the agreed format. Say something to try again.";
            return;
        }

        conversation.Add(new ChatMessage(ChatRole.Assistant, raw));
        state.Transcript.Add(new InterviewTurn(InterviewSpeaker.Alembic, reply.Reply));

        Merge(state, reply.Proposals);

        // Readiness is checked against the Draft, not taken on the model's word: the model reports
        // that it believes this pass settled, and the state machine decides whether it is.
        state.ReadyForReview = reply.ReadyForReview && state.Missing().Count == 0;
    }

    /// <summary>
    /// The per-turn statement of what is already established and what this pass still owes.
    /// </summary>
    /// <remarks>
    /// Values, not just names, unlike the framework's held-context declaration: there the values
    /// are a user's private data and naming them would leak; here they are prose Alembic itself
    /// wrote, and the model needs to see it to revise it rather than propose it again.
    /// </remarks>
    private static string DescribeState(InterviewState state)
    {
        List<string> lines = ["[CONFIGURATION SO FAR]"];

        Append(lines, "intentName", state.Intent.Name);
        Append(lines, "intentDescription", state.Intent.Description);
        Append(lines, "intentLabel", state.Intent.Label);
        Append(lines, "intentDefaultValue", state.Intent.DefaultValue);
        Append(lines, "agentTarget", state.Agent.Target);
        Append(lines, "agentPersonality", state.Agent.Personality);

        if (lines.Count == 1)
            lines.Add("Nothing is settled yet. This is the opening of the interview.");

        IReadOnlyList<string> missing = state.Missing();

        lines.Add(missing.Count == 0
            ? "Every field this pass owns is proposed. Confirm your understanding and set readyForReview."
            : $"Still unproposed: {string.Join(", ", missing)}.");

        return string.Join("\n", lines);

        static void Append(List<string> into, string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                into.Add($"{name}: {value}");
        }
    }

    /// <summary>
    /// Writes a turn's proposals into the state, ignoring anything blank.
    /// </summary>
    /// <remarks>
    /// Overwrites rather than fills: the interview is expected to revise its own earlier proposals
    /// as it learns more, and a merge that refused to overwrite would freeze the first guess.
    /// </remarks>
    private static void Merge(InterviewState state, FunctionalPassProposals? proposals)
    {
        if (proposals is null)
            return;

        state.Intent.Name = Pick(proposals.IntentName, state.Intent.Name);
        state.Intent.Description = Pick(proposals.IntentDescription, state.Intent.Description);
        state.Intent.Label = Pick(proposals.IntentLabel, state.Intent.Label);
        state.Intent.DefaultValue = Pick(proposals.IntentDefaultValue, state.Intent.DefaultValue);
        state.Agent.Target = Marked(TargetMarker, Pick(proposals.AgentTarget, state.Agent.Target));
        state.Agent.Personality = Marked(PersonalityMarker, Pick(proposals.AgentPersonality, state.Agent.Personality));

        if (!string.IsNullOrWhiteSpace(proposals.Language))
            state.Agent.Language = proposals.Language;

        state.Agent.ID = state.Intent.Name;

        static string? Pick(string? proposed, string? existing) =>
            string.IsNullOrWhiteSpace(proposed) ? existing : proposed.Trim();
    }

    /// <summary>
    /// Guarantees a domain-layer section carries its label.
    /// </summary>
    /// <remarks>
    /// Both composed layers use the same four labels, which is exactly why the framework fences the
    /// two: a domain layer arriving unlabelled leaves half the composed prompt without the section
    /// markers the other half has. The label is structure, not authorship — it says which section
    /// this is, not what it means — so it is guaranteed here rather than asked of the model, which
    /// would make a structural invariant depend on a model remembering a formatting rule.
    /// <para>
    /// Idempotent, and applied only to prose the interview authored. An imported agent's prose is
    /// the client's and is never rewritten, marker or no marker.
    /// </para>
    /// </remarks>
    private static string? Marked(string marker, string? value) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith(marker, StringComparison.Ordinal)
            ? value
            : $"{marker} {value}";

    /// <summary>
    /// Reads the model's reply, tolerating a markdown fence around it.
    /// </summary>
    private static FunctionalPassReply? Parse(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Take the outermost braces rather than trimming fences by pattern: it costs the same and
        // survives any preamble the model puts in front, not only the fences we thought to expect.
        int start = raw.IndexOf('{');
        int end = raw.LastIndexOf('}');

        if (start < 0 || end <= start)
            return null;

        try
        {
            FunctionalPassReply? reply = JsonSerializer.Deserialize<FunctionalPassReply>(
                raw[start..(end + 1)], ResponseOptions);

            return string.IsNullOrWhiteSpace(reply?.Reply) ? null : reply;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Proposes a class name from an intent name, by the framework's own naming convention.
    /// </summary>
    private static string? ProposeClassName(string? intentName, string suffix) =>
        string.IsNullOrWhiteSpace(intentName)
            ? null
            : $"{char.ToUpperInvariant(intentName[0])}{intentName[1..]}{suffix}";

    /// <summary>
    /// The model's per-turn reply.
    /// </summary>
    /// <param name="Reply">What to say to the client.</param>
    /// <param name="Proposals">Fields proposed this turn, if any.</param>
    /// <param name="ReadyForReview">Whether the model believes this pass is settled.</param>
    private sealed record FunctionalPassReply(
        [property: JsonPropertyName("reply")] string Reply,
        [property: JsonPropertyName("proposals")] FunctionalPassProposals? Proposals,
        [property: JsonPropertyName("readyForReview")] bool ReadyForReview);

    /// <summary>
    /// The fields the functional pass may write. Instructions and Formatting are absent by
    /// construction: they speak about tools, and the toolkit does not exist during this pass.
    /// </summary>
    private sealed record FunctionalPassProposals(
        [property: JsonPropertyName("intentName")] string? IntentName,
        [property: JsonPropertyName("intentDescription")] string? IntentDescription,
        [property: JsonPropertyName("intentLabel")] string? IntentLabel,
        [property: JsonPropertyName("intentDefaultValue")] string? IntentDefaultValue,
        [property: JsonPropertyName("agentTarget")] string? AgentTarget,
        [property: JsonPropertyName("agentPersonality")] string? AgentPersonality,
        [property: JsonPropertyName("language")] string? Language);
}
