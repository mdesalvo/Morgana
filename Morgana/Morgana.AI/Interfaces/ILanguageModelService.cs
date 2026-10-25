using Microsoft.Extensions.AI;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for Large Language Model (LLM) interactions across the three tiers, each served by the provider it names.
/// Provides the stateless completion of the framework actors and the Microsoft.Extensions.AI IChatClient of each tier.
/// </summary>
/// <remarks>
/// <para><strong>Usage Patterns:</strong></para>
/// <list type="bullet">
/// <item><term>Actors</term><description>GuardActor, ClassifierActor use CompleteWithSystemPromptAsync for stateless operations</description></item>
/// <item><term>Agents</term><description>MorganaAgent uses the IChatClient of its declared tier (via GetChatClient(tier)) for stateful conversations with tool calling</description></item>
/// </list>
/// </remarks>
public interface ILanguageModelService
{
    /// <summary>
    /// Performs a completion with an explicit system prompt and user prompt, on the <see cref="FrameworkTier"/>.
    /// Commonly used by actors for stateless LLM operations (classification, guard checks, etc.).
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation (used for logging, not history)</param>
    /// <param name="systemPrompt">System prompt defining LLM behavior and role</param>
    /// <param name="userPrompt">User message to process</param>
    /// <returns>LLM response text (typically JSON for structured operations)</returns>
    /// <remarks>
    /// <para><strong>Stateless vs Stateful:</strong></para>
    /// <para>This method is stateless—it doesn't rely on conversation history. Each call is independent,
    /// making it suitable for classification, guard checks and other single-turn operations.</para>
    /// <para><strong>JSON Response Pattern:</strong></para>
    /// <para>Most actors using this method expect JSON responses. The system prompt should instruct
    /// the LLM to respond only with JSON (no markdown, no preamble) for reliable parsing.</para>
    /// </remarks>
    Task<string> CompleteWithSystemPromptAsync(string conversationId, string systemPrompt, string userPrompt);

    /// <summary>
    /// Gets the Microsoft.Extensions.AI IChatClient that serves a specific <see cref="Records.LLMTier"/>.
    /// Used by <c>MorganaAgentAdapter</c> to build each agent's <c>AIAgent</c> on the model its
    /// <c>[RequiresLLMTier]</c> attribute declares, rather than a single process-wide client.
    /// </summary>
    /// <param name="tier">Power/cost tier to resolve a client for.</param>
    /// <returns>IChatClient instance for the requested tier.</returns>
    IChatClient GetChatClient(Records.LLMTier tier);

    /// <summary>
    /// Gets the dust of a specific <see cref="Records.LLMTier"/>: the price that the provider of that tier declares
    /// for the tier's model, converted into dust.
    /// </summary>
    /// <param name="tier">Power/cost tier to resolve the dust for.</param>
    /// <returns>MagicDustPricing for the requested tier's model.</returns>
    Records.MagicDustPricing GetMorganaDust(Records.LLMTier tier);

    /// <summary>
    /// Gets the provider that serves a specific <see cref="Records.LLMTier"/>: callers ask it how to read the failures
    /// thrown by that tier's client.
    /// </summary>
    /// <param name="tier">Power/cost tier to resolve the provider for.</param>
    /// <returns>The <see cref="MorganaLanguageModel"/> that the tier names.</returns>
    MorganaLanguageModel GetLanguageModel(Records.LLMTier tier);

    /// <summary>
    /// The tier that Morgana's own actors (Guard, Classifier, Presenter, ChannelAdapter) run on through
    /// <see cref="CompleteWithSystemPromptAsync"/>, as <c>Morgana:ActorSystem:Tier</c> names it.
    /// </summary>
    Records.LLMTier FrameworkTier { get; }
}
