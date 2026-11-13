using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using Morgana.AI.LanguageModels.Anthropic;
using Morgana.AI.LanguageModels.AzureOpenAI;
using Morgana.AI.LanguageModels.Google;
using Morgana.AI.LanguageModels.Ollama;
using Morgana.AI.LanguageModels.OpenAI;
using Morgana.AI.Services;
using Morgana.Contracts;
using PromptHarness.Infrastructure.Wiring;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>Outcome of one judged proposition.</summary>
/// <param name="Holds">Whether the judge found the proposition true of the response.</param>
/// <param name="Reason">The judge's one-line justification, surfaced in failure messages.</param>
public sealed record JudgeVerdict(bool Holds, string Reason);

/// <summary>
/// Evaluates natural-language propositions about a response, the properties no structural assertion can reach.
/// It runs on the deployment's own <see cref="Morgana.AI.Records.LLMTier.Efficiency"/> tier whatever <c>Morgana:ActorSystem:Tier</c> says, so a verdict
/// never moves with the deployment; it reads exactly what a user saw: the tool trace would let it judge on evidence the user never had.
/// </summary>
public sealed class LLMJudge
{
    /// <summary>The options the verdict is read back with.</summary>
    private static readonly JsonSerializerOptions VerdictSerializerOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The judge itself, an agent composed like Morgana's, on the Efficiency tier.</summary>
    private readonly Lazy<Task<AIAgent>> judgeAgent;

    /// <summary>Where the judge's prose and the template of the judged turn come from.</summary>
    private readonly HarnessPromptService harnessPromptService;

    /// <summary>Holds the judge to be composed over the tiers of the deployment under test beside the prose it reads each judged turn from.</summary>
    /// <exception cref="InvalidOperationException">Thrown, on the first judgement, when the Efficiency tier is not configured.</exception>
    private LLMJudge(ILanguageModelService llmService, HarnessPromptService harnessPromptService)
    {
        // The prose of every judged turn, read at each judgement.
        this.harnessPromptService = harnessPromptService;

        // The instructions are read from morgana.json asynchronously, so the agent is composed at the first judgement.
        judgeAgent = new Lazy<Task<AIAgent>>(() => ComposeJudgeAgentAsync(llmService));
    }

    /// <summary>Builds a judge over the same tiers and credentials the instance under test uses, on the five providers Morgana ships.</summary>
    /// <exception cref="InvalidOperationException">Thrown when a tier is configured in a way the host would refuse.</exception>
    public static LLMJudge Create(IConfiguration configuration, ILoggerFactory loggerFactory)
    {
        // The resolver gives the judge Morgana's own policies and the tiers their framework prompts; the judge never
        // resolves an agent's prompt, so a standalone chain is enough where the host builds the whole stack.
        IAgentConfigurationService agentConfigurationService =
            new EmbeddedAgentConfigurationService(loggerFactory.CreateLogger("Harness.Judge"));
        IPromptResolverService promptResolverService =
            new ConfigurationPromptResolverService(agentConfigurationService);

        // The providers the host finds in its plugins/, built here by hand: the judge reads the same tiers.
        MorganaLanguageModel[] languageModels =
        [
            new AnthropicLanguageModel(loggerFactory),
            new AzureOpenAILanguageModel(loggerFactory),
            new GoogleLanguageModel(loggerFactory),
            new OllamaLanguageModel(loggerFactory),
            new OpenAILanguageModel(loggerFactory)
        ];
        // The tiers of the deployment under test, built as the host builds them, so a tier the host refuses is refused here too.
        ILanguageModelService llmService = new ConfigurationLanguageModelService(configuration, languageModels, promptResolverService, loggerFactory);

        // The judge's prose is composed over the same resolver that gives it Morgana's own policies.
        HarnessPromptService harnessPromptService = new HarnessPromptService(promptResolverService);

        // The agent itself is composed at the first judgement, when morgana.json is read.
        return new LLMJudge(llmService, harnessPromptService);
    }

    /// <summary>Composes the judge agent on the Efficiency client with the primer and the judge's own prose as instructions.</summary>
    private async Task<AIAgent> ComposeJudgeAgentAsync(ILanguageModelService llmService)
    {
        // Resolved by tier and not by the framework tier: the judge's model must not move with
        // Morgana:ActorSystem:Tier, which belongs to the deployment under test.
        IChatClient chatClient = llmService.GetChatClient(Morgana.AI.Records.LLMTier.Efficiency);

        // The judge is an agent whose instructions are the composed primer and its own prose.
        return chatClient.AsAIAgent(new ChatClientAgentOptions
        {
            Id = "harness-judge",
            Name = "Judge",
            ChatOptions = new ChatOptions { Instructions = await harnessPromptService.ComposeJudgeInstructionsAsync() }
        });
    }

    /// <summary>Judges every proposition of a turn and returns one failure message per verdict that did not come out as the scenario requires.</summary>
    public async Task<IReadOnlyList<string>> EvaluateAsync(TurnDefinition turnDefinition, TurnResult turn)
        => await EvaluateAsync(turnDefinition.Judge, turnDefinition.JudgeNot, turn.UserMessage, turn.Text, turn.QuickReplies, turn.Message.RichCard);

    /// <summary>
    /// Judges propositions about a bare <see cref="ChannelMessage"/> that never went through a scripted turn.
    /// Morgana's presentation has no <see cref="TurnResult"/> behind it, since no turn ran to produce one.
    /// </summary>
    public async Task<IReadOnlyList<string>> EvaluateAsync(IReadOnlyList<string>? judge, IReadOnlyList<string>? judgeNot, ChannelMessage message)
        => await EvaluateAsync(judge, judgeNot, null, message.Text ?? string.Empty, message.QuickReplies ?? [], message.RichCard);

    /// <summary>Judges every proposition against what the user saw: their own message and the response's three facets.</summary>
    /// <param name="userMessage">What the user said or pressed; null where the response opened the conversation unasked.</param>
    private async Task<IReadOnlyList<string>> EvaluateAsync(
        IReadOnlyList<string>? judge, IReadOnlyList<string>? judgeNot, string? userMessage, string text, IReadOnlyList<QuickReply> quickReplies, RichCard? richCard)
    {
        // One line per proposition that did not come out as required, both polarities together.
        List<string> failures = [];

        // "judge:" propositions must all hold — a failure is any one the judge found false.
        foreach (string proposition in judge ?? [])
        {
            JudgeVerdict? verdict = await VerdictOrFailureAsync("judge", proposition, failures, userMessage, text, quickReplies, richCard);
            if (verdict is { Holds: false })
                failures.Add($"judge: \"{proposition}\" did not hold — {verdict.Reason}");
        }

        // "judgeNot:" propositions must all fail to hold: the polarity is inverted and a proposition the judge finds true is the failure.
        foreach (string proposition in judgeNot ?? [])
        {
            JudgeVerdict? verdict = await VerdictOrFailureAsync("judgeNot", proposition, failures, userMessage, text, quickReplies, richCard);
            if (verdict is { Holds: true })
                failures.Add($"judgeNot: \"{proposition}\" held but must not — {verdict.Reason}");
        }

        // One line per proposition that failed or could not be judged.
        return failures;
    }

    /// <summary>Returns the judge's verdict on a proposition or, where the judge could not answer, records why among the failures and returns null.</summary>
    /// <param name="polarity">The scenario key the proposition was declared under, named in the failure.</param>
    private async Task<JudgeVerdict?> VerdictOrFailureAsync(
        string polarity, string proposition, List<string> failures, string? userMessage, string text, IReadOnlyList<QuickReply> quickReplies, RichCard? richCard)
    {
        try
        {
            // The verdict the scenario's polarity is checked against.
            return await JudgePropositionAsync(proposition, userMessage, text, quickReplies, richCard);
        }
        catch (InvalidOperationException ex)
        {
            // An unjudged proposition fails whatever its polarity: a judgeNot left unanswered would
            // otherwise read as a pass. The line says why, since the fault is the rig's and not the agent's.
            failures.Add($"{polarity}: \"{proposition}\" could not be judged — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The waits of the judging attempts: none, then a short one, then a longer one.
    /// Three entries mean three attempts, so the list is the whole retry policy with no separate count to keep in step.
    /// </summary>
    private static readonly TimeSpan[] JudgeRetryWaits =
        [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(6)];

    /// <summary>Judges a single proposition, retrying twice, with waits, before giving up.</summary>
    /// <exception cref="InvalidOperationException">Thrown when no attempt produced a verdict, naming the last failure.</exception>
    private async Task<JudgeVerdict> JudgePropositionAsync(string proposition, string? userMessage, string text, IReadOnlyList<QuickReply> quickReplies, RichCard? richCard)
    {
        // What the judge writes where the screen shows nothing in that place.
        string nothingShown = harnessPromptService.ResolvePromptInjection(HarnessPromptService.JudgePromptId, "NothingShown");

        // Exactly what a user would see: their own message, the text, button labels and the card's
        // own rendered content — never the tool trace, the context accesses or anything else only the
        // structural layer is allowed to reach. The card is rendered through the same RichCardText the
        // structural layer reads, so a proposition and a richCardContains can never be told different things.
        string buttons = quickReplies.Count == 0
            ? nothingShown
            : string.Join(" | ", quickReplies.Select(reply => reply.Label));
        string card = richCard is null
            ? nothingShown
            : RichCardText.Flatten(richCard);
        string judgedTurn = harnessPromptService.ResolvePromptInjection(
            HarnessPromptService.JudgePromptId, "JudgedTurn",
            ("said", userMessage ?? nothingShown), ("text", text), ("buttons", buttons), ("card", card), ("proposition", proposition));

        // Three attempts, spaced. Two of the failures this exists for are answers outside the verdict's
        // schema, which an immediate retry fixes; the third is the provider itself refusing under load (429, 529),
        // against which two calls in the same instant are one call. The waits are what separate a
        // busy provider from a broken judge and a paid run must not be lost to the former.
        string lastFailure = "no answer";

        foreach (TimeSpan wait in JudgeRetryWaits)
        {
            try
            {
                // Each proposition is judged on its own session: no verdict may lean on another.
                AIAgent agent = await judgeAgent.Value;
                AgentSession session = await agent.CreateSessionAsync();

                // One synchronous answer under the verdict's schema: the judge is an oracle, never a stream.
                AgentResponse<JudgeVerdict> response = await agent.RunAsync<JudgeVerdict>(judgedTurn, session, VerdictSerializerOptions);

                // An answer that still carries no verdict is an unusable one, retried like any other.
                return response.Result ?? throw new JsonException("Judge returned no verdict");
            }
            catch (Exception ex)
            {
                // Every attempt is caught, the last one included: an exception escaping into ScenarioRunner would abort a paid run
                // and read as a broken scenario. The first line of the failure is kept for the message that ends the attempts.
                lastFailure = $"{ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
            }

            // A failed attempt waits its own entry before the next one: none after the first, so the first two attempts are back to back.
            // The longest wait comes after the last attempt, with none left to follow it.
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);
        }

        // A judge that cannot answer gives no verdict at all, never a false one: a made-up "does not
        // hold" would pass every judgeNot. It names what went wrong, because the fault is the rig's and
        // the reader has to be able to tell it from a verdict on the agent.
        throw new InvalidOperationException($"the judge could not be reached or returned an unusable answer ({lastFailure})");
    }
}