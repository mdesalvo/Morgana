using Microsoft.Extensions.AI;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for Large Language Model (LLM) interactions across multiple providers.
/// Provides conversation-scoped completion APIs and access to Microsoft.Extensions.AI IChatClient.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service abstracts LLM provider specifics (Anthropic, Azure OpenAI, Ollama, OpenAI, ...) and provides
/// a unified interface for all LLM interactions in the Morgana framework. It manages conversation
/// history, prompt formatting, and provider-specific API calls.</para>
/// <para><strong>Usage Patterns:</strong></para>
/// <list type="bullet">
/// <item><term>Actors</term><description>GuardActor, ClassifierActor use CompleteWithSystemPromptAsync for stateless operations</description></item>
/// <item><term>Agents</term><description>MorganaAgent uses IChatClient (via GetChatClient) for stateful conversations with tool calling</description></item>
/// </list>
/// </remarks>
public interface ILLMService
{
    /// <summary>
    /// Performs a completion with an explicit system prompt and user prompt.
    /// Commonly used by actors for stateless LLM operations (classification, guard checks, etc.).
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation (used for logging, not history)</param>
    /// <param name="systemPrompt">System prompt defining LLM behavior and role</param>
    /// <param name="userPrompt">User message to process</param>
    /// <returns>LLM response text (typically JSON for structured operations)</returns>
    /// <remarks>
    /// <para><strong>Stateless vs Stateful:</strong></para>
    /// <para>This method is stateless—it doesn't rely on conversation history. Each call is independent,
    /// making it suitable for classification, guard checks, and other single-turn operations.</para>
    /// <para><strong>JSON Response Pattern:</strong></para>
    /// <para>Most actors using this method expect JSON responses. The system prompt should instruct
    /// the LLM to respond only with JSON (no markdown, no preamble) for reliable parsing.</para>
    /// </remarks>
    Task<string> CompleteWithSystemPromptAsync(string conversationId, string systemPrompt, string userPrompt);

    /// <summary>
    /// Gets the underlying Microsoft.Extensions.AI IChatClient for advanced scenarios.
    /// Used by MorganaAgentAdapter to create AIAgent instances with tool calling support.
    /// </summary>
    /// <returns>IChatClient instance configured for the active LLM provider</returns>
    /// <remarks>
    /// <para><strong>Purpose:</strong></para>
    /// <para>The IChatClient provides access to advanced features like tool calling, streaming, and
    /// fine-grained control over LLM interactions. It's used by the agent system but typically not
    /// by actors directly.</para>
    /// <para><strong>Tool Calling:</strong></para>
    /// <para>The IChatClient enables agents to call tools during LLM interactions. The LLM decides
    /// when to invoke tools based on the conversation context, and the agent framework handles
    /// tool execution and result injection back into the conversation.</para>
    /// </remarks>
    IChatClient GetChatClient();

    /// <summary>
    /// Gets the prompt resolver service associated with this LLM service.
    /// Provides access to prompt templates for actors and agents.
    /// </summary>
    /// <returns>IPromptResolverService instance</returns>
    /// <remarks>
    /// <para><strong>Purpose:</strong></para>
    /// <para>This accessor provides a convenient way for components that have ILLMService to also
    /// access the prompt resolver without requiring a separate DI injection.</para>
    /// </remarks>
    IPromptResolverService GetPromptResolverService();
}