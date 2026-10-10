using Microsoft.Extensions.AI;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for Large Language Model (LLM) interactions across multiple providers.
/// Provides conversation-scoped completion APIs and access to Microsoft.Extensions.AI IChatClient.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service abstracts LLM provider specifics (Anthropic, Azure OpenAI, Ollama, OpenAI, ...) and provides
/// a unified interface for all LLM interactions in the Morgana framework. Each tier is served by its own
/// provider, so one installation can mix them.</para>
/// <para><strong>Usage Patterns:</strong></para>
/// <list type="bullet">
/// <item><term>Actors</term><description>GuardActor, ClassifierActor use CompleteWithSystemPromptAsync for stateless operations</description></item>
/// <item><term>Agents</term><description>MorganaAgent uses the IChatClient of its declared tier (via GetChatClient(tier)) for stateful conversations with tool calling</description></item>
/// </list>
/// </remarks>
public interface ILLMService
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
    /// Gets the Microsoft.Extensions.AI IChatClient configured for a specific <see cref="Records.LLMTier"/>.
    /// Used by <c>MorganaAgentAdapter</c> to build each agent's <c>AIAgent</c> on the model its
    /// <c>[RequiresLLMTier]</c> attribute declares, rather than a single process-wide client.
    /// </summary>
    /// <param name="tier">Power/cost tier to resolve a client for.</param>
    /// <returns>IChatClient instance for the requested tier.</returns>
    IChatClient GetChatClient(Records.LLMTier tier);

    /// <summary>
    /// True when the provider that serves <paramref name="tier"/> honours a request naming the one tool that the model
    /// must call. When it does not, a turn that the model closed without Reply is closed by asking for structured output instead.
    /// </summary>
    bool CanForceToolCall(Records.LLMTier tier);

    /// <summary>
    /// Gets the dust pricing for a specific <see cref="Records.LLMTier"/> — the pricing embedded
    /// in that tier's <see cref="Records.TierDefinition"/>, not a single process-wide value.
    /// </summary>
    /// <param name="tier">Power/cost tier to resolve pricing for.</param>
    /// <returns>MagicDustPricing for the requested tier's model.</returns>
    Records.MagicDustPricing GetPricing(Records.LLMTier tier);

    /// <summary>
    /// The tier that Morgana's own actors (Guard, Classifier, Presenter, ChannelAdapter) run on,
    /// named by <c>Morgana:ActorSystem:Tier</c> and <see cref="Records.LLMTier.Efficiency"/> where it names none.
    /// </summary>
    Records.LLMTier FrameworkTier { get; }
}
