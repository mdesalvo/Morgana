using System.Text.Json;
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
/// <item><term>Actors</term><description>Guard, Classifier, Presenter and ChannelAdapter use CompleteStructuredAsync for stateless operations</description></item>
/// <item><term>Agents</term><description>MorganaAgent uses the IChatClient of its declared tier (via GetChatClient(tier)) for stateful conversations with tool calling</description></item>
/// </list>
/// </remarks>
public interface ILLMService
{
    /// <summary>
    /// Asks the <see cref="FrameworkTier"/> one stateless question and reads the answer as a <typeparamref name="T"/>, whose shape the provider is held to by a JSON schema.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation (used for attribution, not history)</param>
    /// <param name="systemPrompt">System prompt defining LLM behavior and role</param>
    /// <param name="userPrompt">User message to process</param>
    /// <param name="responseSchema">The schema that the answer must satisfy; the one derived from <typeparamref name="T"/> where null. Passed to narrow what the type alone allows.</param>
    /// <returns>The answer, or null when it does not deserialize into <typeparamref name="T"/>.</returns>
    /// <remarks>
    /// A provider-level content filter rejection and any other failure of the call are thrown, so that each caller applies its own fallback.
    /// </remarks>
    Task<T?> CompleteStructuredAsync<T>(string conversationId, string systemPrompt, string userPrompt, JsonElement? responseSchema = null);

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
