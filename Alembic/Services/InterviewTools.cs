using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Model;
using Morgana.Contracts;

namespace Alembic.Services;

/// <summary>
/// The tools Alembic calls while conducting a pass.
/// </summary>
/// <remarks>
/// <para>
/// Every method here returns a sentence <b>to the model</b>, not to the client. That return channel
/// is the point of having tools at all rather than a structured reply: a section that comes back
/// the wrong shape is reported to Alembic in the same turn, and it corrects itself before the
/// client ever sees anything. A single malformed structured reply, by contrast, costs the client a
/// turn of their own interview.
/// </para>
/// <para>
/// The write tools also carry the <b>section labels</b>. Both composed layers use the same four,
/// which is exactly why the framework fences them, so a domain layer arriving unlabelled leaves
/// half the prompt without the markers the other half has. A label says which section this is, not
/// what it means: it is structure, and structure is not left to a model remembering a rule.
/// </para>
/// <para>
/// Which of these exist in a given pass is decided by <c>alembic.json</c>, not by prose. The
/// functional pass has no tool for an agent's instructions or formatting, so it cannot write them
/// — the constraint is the absence of a tool rather than a sentence asking for restraint.
/// </para>
/// </remarks>
public class InterviewTools
{
    /// <summary>Section label carried by an agent's Target.</summary>
    private const string TargetMarker = "[TARGET]";

    /// <summary>Section label carried by an agent's Personality.</summary>
    private const string PersonalityMarker = "[PERSONALITY]";

    private readonly InterviewState state;
    private readonly IDraftStateService draftStateService;
    private readonly IDraftValidationService draftValidationService;
    private readonly IRecapService recapService;

    /// <summary>
    /// Binds the toolset to one interview.
    /// </summary>
    /// <param name="state">The interview these tools write into.</param>
    /// <param name="draftStateService">The domain being built or evolved.</param>
    /// <param name="draftValidationService">The deterministic checks.</param>
    /// <param name="recapService">Composes the prompt the authored agent will really read.</param>
    public InterviewTools(
        InterviewState state,
        IDraftStateService draftStateService,
        IDraftValidationService draftValidationService,
        IRecapService recapService)
    {
        this.state = state;
        this.draftStateService = draftStateService;
        this.draftValidationService = draftValidationService;
        this.recapService = recapService;
    }

    /// <summary>
    /// Records the intent that routes to this agent.
    /// </summary>
    public string SetIntent(string name, string description, string label, string defaultValue)
    {
        string cleanName = (name ?? string.Empty).Trim();

        state.Intent.Name = cleanName;
        state.Intent.Description = description?.Trim();
        state.Intent.Label = label?.Trim();
        state.Intent.DefaultValue = defaultValue?.Trim();
        state.Agent.ID = cleanName;

        // Reported rather than corrected: the name is the client's domain vocabulary, and silently
        // rewriting it would leave Alembic telling them one thing while the configuration says
        // another. A shape complaint it can act on is worth more than a fix it never learns about.
        bool bareLowercaseWord = cleanName.Length > 0
                                 && cleanName.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c));

        return bareLowercaseWord
            ? $"Intent recorded as '{cleanName}'."
            : $"Intent recorded as '{cleanName}', but that is not a bare lowercase word. "
              + "It becomes a C# attribute argument and a prompt ID, so call this tool again with one that is.";
    }

    /// <summary>
    /// Records the agent's Target section.
    /// </summary>
    public string SetAgentTarget(string target)
    {
        state.Agent.Target = Marked(TargetMarker, target);
        return Shaped("Target", target, 2, 4);
    }

    /// <summary>
    /// Records the agent's Personality section.
    /// </summary>
    public string SetAgentPersonality(string personality)
    {
        state.Agent.Personality = Marked(PersonalityMarker, personality);
        return Shaped("Personality", personality, 2, 3);
    }

    /// <summary>
    /// Attaches buttons to the question about to be asked.
    /// </summary>
    public string SetChoices(string choices)
    {
        try
        {
            List<QuickReply>? parsed = JsonSerializer.Deserialize<List<QuickReply>>(
                choices, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (parsed is not { Count: > 0 })
                return "No choices recorded: the payload held no buttons.";

            state.PendingChoices.Clear();
            state.PendingChoices.AddRange(parsed);

            return $"{parsed.Count} choices will be drawn under your question. "
                   + "The client's text box stays open, so they may still answer in their own words.";
        }
        catch (JsonException ex)
        {
            return $"No choices recorded: the payload is not a JSON array of buttons ({ex.Message}).";
        }
    }

    /// <summary>
    /// Returns the intents already in the domain.
    /// </summary>
    public string GetExistingIntents()
    {
        List<IntentDraft> existing =
            [.. (draftStateService.Current?.Intents ?? [])
                .Where(i => !string.Equals(i.Name, state.Intent.Name, StringComparison.OrdinalIgnoreCase))];

        return existing.Count == 0
            ? "The domain holds no other intents yet: this is the first, and nothing can collide with it."
            : "Intents already in this domain, with the descriptions the classifier weighs yours against:\n"
              + string.Join("\n", existing.Select(i => $"- {i.Name}: {i.Description}"));
    }

    /// <summary>
    /// Returns the prompt this agent's model will really read.
    /// </summary>
    public async Task<string> GetComposedPrompt()
    {
        if (string.IsNullOrWhiteSpace(state.Agent.Target))
            return "Nothing to compose yet: the agent has no target.";

        AgentRecap recap = await recapService.ComposeAsync(state.Agent);

        return "This is the whole of what this agent's model will read:\n\n" + recap.SystemPrompt;
    }

    /// <summary>
    /// Returns everything wrong with this agent that is decidable without a model.
    /// </summary>
    /// <remarks>
    /// Checked against a probe domain — what the client already has, plus the agent under
    /// construction — because half of these rules are relational: an intent nothing routes to and a
    /// name colliding with a framework prompt are both invisible when an agent is examined alone.
    /// Findings about the client's other agents are filtered out: they are real, but they are not
    /// this pass's business and Alembic cannot fix them from here.
    /// </remarks>
    public string GetFindings()
    {
        if (string.IsNullOrWhiteSpace(state.Intent.Name))
            return "Nothing to check yet: the intent has no name.";

        DomainDraft existing = draftStateService.Current ?? new DomainDraft();

        DomainDraft probe = new DomainDraft
        {
            Intents = [.. existing.Intents, state.Intent],
            Agents = [.. existing.Agents, state.Agent]
        };

        string mine = state.Intent.Name!;

        List<ValidationFinding> findings =
            [.. draftValidationService.Validate(probe)
                .Where(f => f.Where.Contains($"'{mine}'", StringComparison.OrdinalIgnoreCase)
                            || f.Where.StartsWith($"{mine}.", StringComparison.OrdinalIgnoreCase)
                            || f.Where == "domain")];

        return findings.Count == 0
            ? "Nothing to report: every deterministic check passes for this agent."
            : string.Join("\n", findings.Select(f => $"[{f.Severity}] {f.Where}: {f.Message} — {f.Because}"));
    }

    /// <summary>
    /// Declares the pass settled.
    /// </summary>
    /// <remarks>
    /// Believed only as far as the state machine can confirm it. Which fields are set is a fact,
    /// and facts are not a model's to assert.
    /// </remarks>
    public string SetPassCompleted()
    {
        IReadOnlyList<string> missing = state.Missing();

        if (missing.Count > 0)
            return $"Not completed: {string.Join(", ", missing)} still unset. "
                   + (missing.Count == 1 ? "Set it and call this again." : "Set them and call this again.");

        state.ReadyForReview = true;
        return "This pass is settled. Tell the client it is done and what comes next.";
    }

    /// <summary>
    /// Guarantees a section carries its label. Idempotent.
    /// </summary>
    private static string? Marked(string marker, string? value) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith(marker, StringComparison.Ordinal)
            ? value?.Trim()
            : $"{marker} {value.Trim()}";

    /// <summary>
    /// Reports whether a section landed inside the size its doctrine gives it.
    /// </summary>
    /// <remarks>
    /// Recorded either way. The size is a shape, not a gate: a Target of five sentences is still
    /// better than no Target, and Alembic is told so it can tighten rather than blocked so it must.
    /// </remarks>
    private static string Shaped(string section, string? value, int minimum, int maximum)
    {
        int sentences = CountSentences(value);

        return sentences >= minimum && sentences <= maximum
            ? $"{section} recorded."
            : $"{section} recorded, but it runs to {sentences} "
              + (sentences == 1 ? "sentence" : "sentences")
              + $" where this section's shape is {minimum} to {maximum}. Tighten it and call again.";
    }

    /// <summary>
    /// Counts sentences crudely — terminal punctuation followed by a space or the end.
    /// </summary>
    private static int CountSentences(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        string trimmed = value.Trim();
        int count = 0;

        for (int i = 0; i < trimmed.Length; i++)
        {
            if (trimmed[i] is not ('.' or '!' or '?'))
                continue;

            if (i == trimmed.Length - 1 || char.IsWhiteSpace(trimmed[i + 1]))
                count++;
        }

        // Prose that never reaches terminal punctuation is still one sentence, not none.
        return count == 0 ? 1 : count;
    }
}
