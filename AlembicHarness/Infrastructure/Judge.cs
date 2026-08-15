using System.Text.Json;
using Morgana.AI.Interfaces;

namespace AlembicHarness.Infrastructure;

/// <summary>Outcome of one judged proposition.</summary>
/// <param name="Holds">Whether the judge found the proposition true of the text.</param>
/// <param name="Reason">The judge's one-line justification, surfaced in failure messages.</param>
public sealed record JudgeVerdict(bool Holds, string Reason);

/// <summary>
/// Evaluates natural-language propositions about a piece of authored prose — a near-direct port of
/// <c>PromptHarness</c>'s <c>LLMJudge</c>, for the same reason it exists there: Alembic's own
/// self-check inside <c>AgentFormatter</c> is the same conducting session re-reading its own work,
/// which is not independence, and a live run has already shown it can follow a client's explicit
/// request straight past a rule it just wrote. This is a second, separate call that has never seen
/// the interview and has no stake in defending it.
/// </summary>
/// <remarks>
/// Judges the authored <c>Formatting</c>/<c>Instructions</c> prose directly, not a live simulated
/// turn — cheaper and simpler than standing up a running agent, and sufficient for the class of
/// defect it exists to catch: whether the INSTRUCTION a pass wrote itself commits the anti-pattern
/// ("offer one button per open slot"), which is legible straight out of the prose without needing
/// to watch a model act on it.
/// </remarks>
public sealed class Judge
{
    private const string SystemPrompt =
        """
        You are a strict evaluator in a non-regression suite for an AI system that authors
        conversational agent prompts. You are given a piece of authored prose — instructions that
        will govern a deployed agent's behaviour — and a proposition about it.
        Decide whether the proposition is TRUE of that prose.

        Rules:
        - Judge only what the prose actually instructs. Do not infer intent, do not be charitable.
        - If the proposition is only partially true, it is false.
        - The prose may be in any language; judge its meaning, not its language.

        Respond with JSON only, no prose, no code fences:
        {"holds": true|false, "reason": "<one short sentence>"}
        """;

    /// <summary>LLM used for judging, on the cheapest configured tier — the same guarantee
    /// <c>CompleteWithSystemPromptAsync</c> gives <c>PromptHarness</c>'s own judge.</summary>
    private readonly ILLMService llmService;

    public Judge(ILLMService llmService) => this.llmService = llmService;

    /// <summary>Judges a single proposition against a piece of prose, retrying once before giving up.</summary>
    public async Task<JudgeVerdict> EvaluateAsync(string proposition, string prose, CancellationToken cancellationToken = default)
    {
        string userPrompt =
            $"""
             AUTHORED PROSE:
             {prose}

             PROPOSITION:
             {proposition}
             """;

        // Two attempts total, same tolerance PromptHarness's judge applies: a real LLM occasionally
        // answers with something Parse cannot make sense of even under a strict system prompt.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                string answer = await llmService.CompleteWithSystemPromptAsync(
                    $"alembic-harness-judge-{Guid.NewGuid():N}", SystemPrompt, userPrompt);

                return Parse(answer);
            }
            catch (Exception)
            {
                // Both attempts are caught; a judge that cannot answer twice is reported as a
                // failure below rather than propagated, so a flaky judge reads as "the proposition
                // could not be confirmed" and not as an aborted test run.
            }
        }

        return new JudgeVerdict(false, "the judge could not be reached or returned an unusable answer");
    }

    /// <summary>Parses the judge's JSON, tolerating the code fences some models add anyway.</summary>
    private static JudgeVerdict Parse(string answer)
    {
        string payload = answer.Trim();
        if (payload.StartsWith("```", StringComparison.Ordinal))
            payload = payload.Trim('`', ' ', '\n', '\r').TrimStart('j', 's', 'o', 'n').Trim();

        int start = payload.IndexOf('{');
        int end = payload.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new JsonException($"Judge returned no JSON object: {answer}");

        using JsonDocument document = JsonDocument.Parse(payload[start..(end + 1)]);

        if (!document.RootElement.TryGetProperty("holds", out JsonElement holds))
            throw new JsonException($"Judge returned no 'holds' property: {answer}");

        return new JudgeVerdict(
            holds.GetBoolean(),
            document.RootElement.TryGetProperty("reason", out JsonElement reason)
                ? reason.GetString() ?? string.Empty
                : string.Empty);
    }
}
