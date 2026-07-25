using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.Tests.NRT.Scenarios;

namespace Morgana.Tests.NRT.Infrastructure;

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
public sealed class LlmJudge
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

    private LlmJudge(ILLMService llmService) => this.llmService = llmService;

    /// <summary>
    /// Builds a judge over the same provider and credentials the instance under test uses, mirroring
    /// the provider switch in <c>Morgana.Web/Program.cs</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the configured provider is unknown.</exception>
    public static LlmJudge Create(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        IAgentConfigurationService agentConfigurationService =
            new EmbeddedAgentConfigurationService(loggerFactory.CreateLogger("Nrt.Judge"));
        IPromptResolverService promptResolverService =
            new ConfigurationPromptResolverService(agentConfigurationService);

        string provider = configuration["Morgana:LLM:Provider"]
            ?? throw new InvalidOperationException("Morgana:LLM:Provider is not configured.");

        ILLMService llmService = provider.ToLowerInvariant() switch
        {
            "anthropic" => new AI.Abstractions.LLMs.Anthropic(configuration, promptResolverService, loggerFactory),
            "azureopenai" => new AI.Abstractions.LLMs.AzureOpenAI(configuration, promptResolverService, loggerFactory),
            "ollama" => new AI.Abstractions.LLMs.Ollama(configuration, promptResolverService, loggerFactory),
            "openai" => new AI.Abstractions.LLMs.OpenAI(configuration, promptResolverService, loggerFactory),
            _ => throw new InvalidOperationException($"LLM Provider '{provider}' not supported by the NRT judge.")
        };

        return new LlmJudge(llmService);
    }

    /// <summary>
    /// Judges every proposition of a turn and returns one failure message per verdict that did not
    /// come out as the scenario requires.
    /// </summary>
    public async Task<IReadOnlyList<string>> EvaluateAsync(TurnDefinition turnDefinition, TurnResult turn)
    {
        List<string> failures = [];

        foreach (string proposition in turnDefinition.Judge ?? [])
        {
            JudgeVerdict verdict = await EvaluateAsync(proposition, turn);
            if (!verdict.Holds)
                failures.Add($"judge: \"{proposition}\" did not hold — {verdict.Reason}");
        }

        foreach (string proposition in turnDefinition.JudgeNot ?? [])
        {
            JudgeVerdict verdict = await EvaluateAsync(proposition, turn);
            if (verdict.Holds)
                failures.Add($"judgeNot: \"{proposition}\" held but must not — {verdict.Reason}");
        }

        return failures;
    }

    /// <summary>Judges a single proposition, retrying once before giving up.</summary>
    private async Task<JudgeVerdict> EvaluateAsync(string proposition, TurnResult turn)
    {
        string userPrompt =
            $"""
             RESPONSE TEXT:
             {turn.Text}

             QUICK REPLY BUTTONS SHOWN: {(turn.QuickReplies.Count == 0 ? "none" : string.Join(" | ", turn.QuickReplies.Select(reply => reply.Label)))}
             RICH CARD SHOWN: {(turn.Message.RichCard is null ? "no" : $"yes, titled \"{turn.Message.RichCard.Title}\"")}

             PROPOSITION:
             {proposition}
             """;

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                string answer = await llmService.CompleteWithSystemPromptAsync(
                    $"nrt-judge-{Guid.NewGuid():N}", SystemPrompt, userPrompt);

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
        if (payload.StartsWith("```", StringComparison.Ordinal))
            payload = payload.Trim('`', ' ', '\n', '\r').TrimStart('j', 's', 'o', 'n').Trim();

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