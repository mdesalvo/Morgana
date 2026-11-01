using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;

namespace PromptHarness.Infrastructure;

/// <summary>
/// Stands where the language model service would for a guard in the deterministic groups: answers every call with a scripted text and records the last call it received.
/// </summary>
public sealed class ScriptedLanguageModelService : ILanguageModelService
{
    /// <summary>What the guard's model answers; may throw or wait.</summary>
    private readonly Func<Task<string>> answer;

    /// <summary>Scripts the guard's answer, ready the moment it is asked; a script that throws fails the call.</summary>
    public ScriptedLanguageModelService(Func<string> answer) : this(() => Task.FromResult(answer()))
    {
    }

    /// <summary>Scripts the guard's answer as the task it replies with.</summary>
    private ScriptedLanguageModelService(Func<Task<string>> answer) => this.answer = answer;

    /// <summary>Scripts a guard whose answer arrives when the test releases it, so concurrent callers meet a judgment in progress.</summary>
    /// <remarks>A factory rather than a public overload: a script that only throws would fit both shapes of answer.</remarks>
    public static ScriptedLanguageModelService AnsweringLater(Func<Task<string>> answer) => new ScriptedLanguageModelService(answer);

    /// <summary>The system prompt of the last call.</summary>
    public string? SystemPrompt { get; private set; }

    /// <summary>The user prompt of the last call.</summary>
    public string? UserPrompt { get; private set; }

    /// <summary>The dust role of the last call.</summary>
    public string? DustRole { get; private set; }

    /// <summary>How many judgments were asked.</summary>
    public int Calls { get; private set; }

    /// <summary>Records the call and answers as scripted.</summary>
    public Task<string> CompleteWithSystemPromptAsync(string conversationId, string systemPrompt, string userPrompt, string? dustRole = null)
    {
        // The last call is what a scenario asserts on.
        SystemPrompt = systemPrompt;
        UserPrompt = userPrompt;
        DustRole = dustRole;
        Calls++;

        // The scripted answer is what the guard's model replies to this call: it may be ready now or arrive later.
        return answer();
    }

    /// <summary>The tier the judgment runs on.</summary>
    public Records.LLMTier FrameworkTier => Records.LLMTier.Economy;

    /// <summary>Never reached by the scenarios.</summary>
    public IChatClient GetChatClient(Records.LLMTier tier)
        => throw new InvalidOperationException("The scripted model serves no chat client");

    /// <summary>Never reached by the scenarios.</summary>
    public Records.MagicDustPricing GetMorganaDust(Records.LLMTier tier)
        => throw new InvalidOperationException("The scripted model serves no dust");

    /// <summary>Reached only by the filter that asks whether a failure is a content refusal, where the throw makes the filter false.</summary>
    public MorganaLanguageModel GetLanguageModel(Records.LLMTier tier)
        => throw new InvalidOperationException("no provider");
}
