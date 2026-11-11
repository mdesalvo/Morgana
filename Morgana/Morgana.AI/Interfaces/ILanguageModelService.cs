using Microsoft.Extensions.AI;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Interfaces;

/// <summary>Serves the three tiers, each on the provider it names: the framework actors' completions and every agent's chat client.</summary>
public interface ILanguageModelService
{
    /// <summary>One stateless completion of <paramref name="userPrompt"/> under <paramref name="systemPrompt"/>, on the <see cref="FrameworkTier"/>.</summary>
    /// <param name="conversationId">The conversation the call is charged to; no history of it is read.</param>
    /// <param name="systemPrompt">The prompt that tells the model its role.</param>
    /// <param name="userPrompt">What the model is asked.</param>
    /// <param name="dustRole">The role the call is charged under on the dust ledger; null charges it under the bare framework role.</param>
    /// <param name="cancellationToken">Stops the call to the model once whoever asked for it stops waiting.</param>
    /// <returns>The model's answer, usually the JSON that the system prompt asked for.</returns>
    Task<string> CompleteWithSystemPromptAsync(string conversationId, string systemPrompt, string userPrompt, string? dustRole = null, CancellationToken cancellationToken = default);

    /// <summary>The chat client of <paramref name="tier"/>, on which an agent declaring that tier is built.</summary>
    IChatClient GetChatClient(Records.LLMTier tier);

    /// <summary>The dust price of <paramref name="tier"/>, converted from what its provider declares for the tier's model.</summary>
    Records.MagicDustPricing GetMorganaDust(Records.LLMTier tier);

    /// <summary>The provider that serves <paramref name="tier"/>, which callers ask how to read the failures of the tier's client.</summary>
    MorganaLanguageModel GetLanguageModel(Records.LLMTier tier);

    /// <summary>The tier that Morgana's own actors run on, as <c>Morgana:ActorSystem:Tier</c> names it.</summary>
    Records.LLMTier FrameworkTier { get; }
}