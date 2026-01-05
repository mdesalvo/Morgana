using Microsoft.Extensions.AI;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for Large Language Model (LLM) interactions across multiple providers.
/// Provides conversation-scoped completion APIs and access to Microsoft.Extensions.AI IChatClient.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>This service abstracts LLM provider specifics (Anthropic, Azure OpenAI, etc.) and provides
/// a unified interface for all LLM interactions in the Morgana framework. It manages conversation
/// history, prompt formatting, and provider-specific API calls.</para>
/// <para><strong>Supported Providers:</strong></para>
/// <list type="bullet">
/// <item><term>Anthropic</term><description>Claude models (Claude Sonnet 4, etc.) via AnthropicService</description></item>
/// <item><term>Azure OpenAI</term><description>GPT models via AzureOpenAIService</description></item>
/// <item><term>Custom</term><description>Extensible for additional providers implementing this interface</description></item>
/// </list>
/// <para><strong>Provider Selection:</strong></para>
/// <para>The active provider is configured in appsettings.json and resolved during DI registration:</para>
/// <code>
/// // appsettings.json
/// {
///   "LLM": {
///     "Provider": "anthropic",  // or "azureopenai"
///     "ApiKey": "...",
///     "Model": "claude-sonnet-4-20250514"
///   }
/// }
/// 
/// // Program.cs
/// builder.Services.AddSingleton&lt;ILLMService&gt;(sp => {
///     string provider = config["LLM:Provider"];
///     return provider.ToLowerInvariant() switch {
///         "anthropic" => new AnthropicService(config, promptResolver),
///         "azureopenai" => new AzureOpenAIService(config, promptResolver),
///         _ => throw new InvalidOperationException("Unsupported provider")
///     };
/// });
/// </code>
/// <para><strong>Conversation Management:</strong></para>
/// <para>All completion methods accept a conversationId parameter. Implementations maintain separate
/// conversation histories per conversationId, enabling context-aware multi-turn interactions.</para>
/// <para><strong>Usage Patterns:</strong></para>
/// <list type="bullet">
/// <item><term>Actors</term><description>GuardActor, ClassifierActor use CompleteWithSystemPromptAsync for stateless operations</description></item>
/// <item><term>Agents</term><description>MorganaAgent uses IChatClient (via GetChatClient) for stateful conversations with tool calling</description></item>
/// </list>
/// </remarks>
public interface ILLMService
{
    /// <summary>
    /// Performs a completion with a single user prompt.
    /// Uses conversation history for context-aware responses.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation for history management</param>
    /// <param name="prompt">User prompt to send to the LLM</param>
    /// <returns>LLM response text</returns>
    /// <remarks>
    /// <para><strong>Usage:</strong></para>
    /// <para>This method is suitable for simple completions where the system prompt is already established
    /// in the conversation history. Less commonly used than CompleteWithSystemPromptAsync in Morgana.</para>
    /// <para><strong>Conversation History:</strong></para>
    /// <para>The implementation maintains a conversation history per conversationId. Each call appends
    /// the user prompt and LLM response to the history, enabling context-aware follow-up messages.</para>
    /// </remarks>
    Task<string> CompleteAsync(string conversationId, string prompt);
    
    /// <summary>
    /// Performs a completion with an explicit system prompt and user prompt.
    /// Commonly used by actors for stateless LLM operations (classification, guard checks, etc.).
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation (used for logging, not history)</param>
    /// <param name="systemPrompt">System prompt defining LLM behavior and role</param>
    /// <param name="userPrompt">User message to process</param>
    /// <returns>LLM response text (typically JSON for structured operations)</returns>
    /// <remarks>
    /// <para><strong>Usage Examples:</strong></para>
    /// <code>
    /// // GuardActor - Content moderation
    /// string systemPrompt = "You are a language guard. Verify if the message contains...";
    /// string userPrompt = "User's message text";
    /// string response = await llmService.CompleteWithSystemPromptAsync(
    ///     conversationId, 
    ///     systemPrompt, 
    ///     userPrompt
    /// );
    /// // Response: {"compliant": true, "violation": null}
    /// 
    /// // ClassifierActor - Intent classification
    /// string systemPrompt = "You are a classifier. Classify this message into: billing|contract|...";
    /// string userPrompt = "Show me my invoices";
    /// string response = await llmService.CompleteWithSystemPromptAsync(
    ///     conversationId, 
    ///     systemPrompt, 
    ///     userPrompt
    /// );
    /// // Response: {"intent": "billing", "confidence": 0.95}
    /// </code>
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
    /// Used by AgentAdapter to create AIAgent instances with tool calling support.
    /// </summary>
    /// <returns>IChatClient instance configured for the active LLM provider</returns>
    /// <remarks>
    /// <para><strong>Purpose:</strong></para>
    /// <para>The IChatClient provides access to advanced features like tool calling, streaming, and
    /// fine-grained control over LLM interactions. It's used by the agent system but typically not
    /// by actors directly.</para>
    /// <para><strong>Usage in AgentAdapter:</strong></para>
    /// <code>
    /// public (AIAgent agent, MorganaContextProvider provider) CreateAgent(Type agentType)
    /// {
    ///     // ... agent setup ...
    ///     
    ///     AIAgent agent = chatClient.CreateAIAgent(
    ///         instructions: instructions,
    ///         name: intent,
    ///         tools: toolAdapter.CreateAllFunctions().ToArray()
    ///     );
    ///     
    ///     return (agent, contextProvider);
    /// }
    /// </code>
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
    /// <para><strong>Usage:</strong></para>
    /// <code>
    /// ILLMService llmService = ...;
    /// Prompt guardPrompt = await llmService.GetPromptResolverService().ResolveAsync("Guard");
    /// </code>
    /// <para><strong>Design Note:</strong></para>
    /// <para>While this creates a coupling between LLM service and prompt resolver, it simplifies
    /// the dependency graph for actors that need both services.</para>
    /// </remarks>
    IPromptResolverService GetPromptResolverService();
}