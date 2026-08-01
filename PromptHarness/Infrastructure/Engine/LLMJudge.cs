using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Wiring;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>Outcome of one judged proposition.</summary>
/// <param name="Holds">Whether the judge found the proposition true of the response.</param>
/// <param name="Reason">The judge's one-line justification, surfaced in failure messages.</param>
public sealed record JudgeVerdict(bool Holds, string Reason);

/// <summary>
/// Evaluates natural-language propositions about a response — the properties no structural
/// assertion can reach, such as "asks for the operand in prose without enumerating options".
/// </summary>
/// <remarks>
/// <para>The judge runs through <see cref="ILLMService.CompleteWithSystemPromptAsync"/>, which by
/// construction uses the cheapest configured tier. That keeps the suite's judging cost proportional
/// to the deployment it is testing, and adds no provider, key or dependency of its own.</para>
///
/// <para>It is deliberately given only what a user would see — text, quick replies, whether a card
/// was rendered. Feeding it the tool trace would let it justify a verdict from evidence the user
/// never had, which is exactly the class of judgement the structural layer already owns.</para>
/// </remarks>
public sealed class LLMJudge
{
    /// <summary>Instruction fixing the judge's output shape and its bias toward the response's own words.</summary>
    private const string SystemPrompt =
        """
        You are a strict evaluator in a non-regression suite for a conversational AI.
        You are given an assistant's response and a proposition about it.
        Decide whether the proposition is TRUE of that response.

        Rules:
        - Judge only what the response actually says. Do not infer intent, do not be charitable.
        - If the proposition is only partially true, it is false.
        - The response may be in any language; judge its meaning, not its language.

        Respond with JSON only, no prose, no code fences:
        {"holds": true|false, "reason": "<one short sentence>"}
        """;

    /// <summary>LLM used for judging, on the cheapest configured tier.</summary>
    private readonly ILLMService llmService;

    private LLMJudge(ILLMService llmService) => this.llmService = llmService;

    /// <summary>
    /// Builds a judge over the same provider and credentials the instance under test uses, mirroring
    /// the provider switch in <c>Morgana.Web/Program.cs</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the configured provider is unknown.</exception>
    public static LLMJudge Create(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        // These two exist only to satisfy the LLM classes' constructors — the judge never resolves
        // an agent's own prompt, so a minimal, standalone chain is enough here rather than the
        // full agent-configuration stack the host itself builds.
        IAgentConfigurationService agentConfigurationService =
            new EmbeddedAgentConfigurationService(loggerFactory.CreateLogger("Harness.Judge"));
        IPromptResolverService promptResolverService =
            new ConfigurationPromptResolverService(agentConfigurationService);

        string provider = configuration["Morgana:LLM:Provider"]
            ?? throw new InvalidOperationException("Morgana:LLM:Provider is not configured.");

        // Mirrors the provider switch in Morgana.Web/Program.cs by hand — there is no shared
        // factory to call into, so a new provider added there has to be added here too.
        ILLMService llmService = provider.ToLowerInvariant() switch
        {
            "anthropic" => new Morgana.AI.Abstractions.LLMs.Anthropic(configuration, promptResolverService, loggerFactory),
            "azureopenai" => new Morgana.AI.Abstractions.LLMs.AzureOpenAI(configuration, promptResolverService, loggerFactory),
            "ollama" => new Morgana.AI.Abstractions.LLMs.Ollama(configuration, promptResolverService, loggerFactory),
            "openai" => new Morgana.AI.Abstractions.LLMs.OpenAI(configuration, promptResolverService, loggerFactory),
            _ => throw new InvalidOperationException($"LLM Provider '{provider}' not supported by the harness judge.")
        };

        return new LLMJudge(llmService);
    }

    /// <summary>
    /// Judges every proposition of a turn and returns one failure message per verdict that did not
    /// come out as the scenario requires.
    /// </summary>
    public async Task<IReadOnlyList<string>> EvaluateAsync(TurnDefinition turnDefinition, TurnResult turn)
        => await EvaluateAsync(turnDefinition.Judge, turnDefinition.JudgeNot, turn.Text, turn.QuickReplies, turn.Message.RichCard);

    /// <summary>
    /// Judges propositions about a bare <see cref="ChannelMessage"/> that never went through a
    /// scripted turn — Presentation has no <see cref="TurnResult"/> behind it, since no
    /// <c>ScenarioRunner</c> turn ever ran to produce one.
    /// </summary>
    public async Task<IReadOnlyList<string>> EvaluateAsync(IReadOnlyList<string>? judge, IReadOnlyList<string>? judgeNot, ChannelMessage message)
        => await EvaluateAsync(judge, judgeNot, message.Text ?? string.Empty, message.QuickReplies ?? [], message.RichCard);

    /// <summary>Judges every proposition against the same three user-visible facets, whatever produced them.</summary>
    private async Task<IReadOnlyList<string>> EvaluateAsync(
        IReadOnlyList<string>? judge, IReadOnlyList<string>? judgeNot, string text, IReadOnlyList<QuickReply> quickReplies, RichCard? richCard)
    {
        List<string> failures = [];

        // "judge:" propositions must all hold — a failure is any one the judge found false.
        foreach (string proposition in judge ?? [])
        {
            JudgeVerdict verdict = await EvaluateAsync(proposition, text, quickReplies, richCard);
            if (!verdict.Holds)
                failures.Add($"judge: \"{proposition}\" did not hold — {verdict.Reason}");
        }

        // "judgeNot:" propositions must all fail to hold — the polarity is inverted from above:
        // here it's the judge finding TRUE that produces a failure message.
        foreach (string proposition in judgeNot ?? [])
        {
            JudgeVerdict verdict = await EvaluateAsync(proposition, text, quickReplies, richCard);
            if (verdict.Holds)
                failures.Add($"judgeNot: \"{proposition}\" held but must not — {verdict.Reason}");
        }

        return failures;
    }

    /// <summary>Judges a single proposition, retrying once before giving up.</summary>
    private async Task<JudgeVerdict> EvaluateAsync(string proposition, string text, IReadOnlyList<QuickReply> quickReplies, RichCard? richCard)
    {
        // Deliberately only what a user would see: text, button labels, and whether a card was
        // shown (with its title, not its full JSON payload) — never the tool trace, the context
        // accesses, or anything else only the structural layer is allowed to reach.
        string userPrompt =
            $"""
             RESPONSE TEXT:
             {text}

             QUICK REPLY BUTTONS SHOWN: {(quickReplies.Count == 0 ? "none" : string.Join(" | ", quickReplies.Select(reply => reply.Label)))}
             RICH CARD SHOWN: {(richCard is null ? "no" : $"yes, titled \"{richCard.Title}\"")}

             PROPOSITION:
             {proposition}
             """;

        // Two attempts total: a real LLM occasionally answers with something Parse cannot make
        // sense of (extra prose, malformed JSON) even under a strict system prompt, and a single
        // retry recovers most of those without masking a genuinely broken judge.
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                string answer = await llmService.CompleteWithSystemPromptAsync(
                    $"harness-judge-{Guid.NewGuid():N}", SystemPrompt, userPrompt);

                return Parse(answer);
            }
            // Every attempt is caught, the last one included. Filtering on `attempt == 1` let the
            // second attempt's exception escape into ScenarioRunner, which aborts the whole run —
            // so an unusable judge answer cost a paid run and was reported as "run aborted", a line
            // that reads like a broken scenario and is not one. It also made the fallback below
            // unreachable, which is the opposite of what it exists for.
            catch (Exception ex)
            {
                _ = ex;
            }
        }

        // A judge that cannot answer is reported as a failure rather than silently skipped: a
        // proposition that stops being evaluated is coverage lost without anyone noticing.
        return new JudgeVerdict(false, "the judge could not be reached or returned an unusable answer");
    }

    /// <summary>Parses the judge's JSON, tolerating the code fences some models add anyway.</summary>
    private static JudgeVerdict Parse(string answer)
    {
        string payload = answer.Trim();
        // The system prompt asks for "no code fences", but some models wrap the JSON in a
        // ```json ... ``` block anyway; this strips the fence markers and a leading "json" hint
        // word character-by-character rather than depending on the fence being well-formed.
        if (payload.StartsWith("```", StringComparison.Ordinal))
            payload = payload.Trim('`', ' ', '\n', '\r').TrimStart('j', 's', 'o', 'n').Trim();

        // Slices out the outermost { ... } rather than assuming the whole trimmed string is valid
        // JSON — tolerant of a stray leading/trailing sentence some models still add despite the
        // "no prose" instruction.
        int start = payload.IndexOf('{');
        int end = payload.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new JsonException($"Judge returned no JSON object: {answer}");

        using JsonDocument document = JsonDocument.Parse(payload[start..(end + 1)]);

        // TryGetProperty, not GetProperty: a verdict missing 'holds' is an unusable answer to be
        // retried, not a KeyNotFoundException thrown from the middle of a turn.
        if (!document.RootElement.TryGetProperty("holds", out JsonElement holds))
            throw new JsonException($"Judge returned no 'holds' property: {answer}");

        return new JudgeVerdict(
            holds.GetBoolean(),
            document.RootElement.TryGetProperty("reason", out JsonElement reason)
                ? reason.GetString() ?? string.Empty
                : string.Empty);
    }
}
