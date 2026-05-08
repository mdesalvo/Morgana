using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Abstractions;

/// <summary>
/// Base implementation of ILLMService providing common LLM interaction patterns.
/// Supports multiple LLM providers (Anthropic, Azure OpenAI, Ollama, OpenAI) via the
/// Microsoft.Extensions.AI abstraction.
/// </summary>
/// <remarks>
/// <para><strong>Architecture:</strong></para>
/// <code>
/// MorganaLLM (abstract base, this file)
///   └── Abstractions/LLMs/
///         ├── Anthropic.cs    (Anthropic Claude models, includes the in-process
///         │                    GuardChatClient that enforces Claude 4.6+ no-prefill)
///         ├── AzureOpenAI.cs  (Azure OpenAI GPT models)
///         ├── Ollama.cs       (Ollama local models)
///         └── OpenAI.cs       (OpenAI GPT models)
/// </code>
/// <para><strong>Key Features:</strong></para>
/// <list type="bullet">
/// <item><term>Conversation Management</term><description>Tracks conversation history per conversationId</description></item>
/// <item><term>Error Handling</term><description>Returns user-friendly error messages from Morgana prompts</description></item>
/// <item><term>JSON Cleanup</term><description>Strips markdown code fences from LLM responses</description></item>
/// <item><term>System Prompt Support</term><description>Explicit system prompt injection for actors</description></item>
/// </list>
/// </remarks>
public class MorganaLLM : ILLMService
{
    /// <summary>
    /// Application configuration used by derived classes to read provider-specific settings
    /// (API keys, endpoints, model names, deployment IDs).
    /// </summary>
    protected readonly IConfiguration configuration;

    /// <summary>
    /// Service for resolving prompt templates by name. Used to load the Morgana framework
    /// prompt at construction and exposed to callers via <see cref="GetPromptResolverService"/>.
    /// </summary>
    protected readonly IPromptResolverService promptResolverService;

    /// <summary>
    /// Morgana framework prompt loaded at construction time. Provides user-facing error message
    /// templates used when LLM calls fail or return unusable content.
    /// </summary>
    protected readonly Records.Prompt morganaPrompt;

    /// <summary>
    /// Microsoft.Extensions.AI chat client for LLM interactions.
    /// Initialized by derived classes (Anthropic, AzureOpenAI, Ollama, OpenAI).
    /// </summary>
    protected IChatClient chatClient;

    /// <summary>
    /// Initializes MorganaLLM abstraction.
    /// Loads the Morgana framework prompt for error message templates.
    /// </summary>
    /// <param name="configuration">Application configuration for provider-specific settings</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    public MorganaLLM(
        IConfiguration configuration,
        IPromptResolverService promptResolverService)
    {
        this.configuration = configuration;
        this.promptResolverService = promptResolverService;

        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets the underlying Microsoft.Extensions.AI chat client.
    /// Used by MorganaAgentAdapter to create AIAgent instances with tool calling support.
    /// </summary>
    /// <returns>IChatClient instance configured for the active provider</returns>
    public IChatClient GetChatClient() => chatClient;

    /// <summary>
    /// Gets the prompt resolver service associated with this LLM service.
    /// </summary>
    /// <returns>IPromptResolverService instance</returns>
    public IPromptResolverService GetPromptResolverService() => promptResolverService;

    /// <summary>
    /// Performs a completion with an explicit system prompt and user message.
    /// Primary method for actors performing stateless LLM operations (classification, guard checks).
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation (used for logging)</param>
    /// <param name="systemPrompt">System prompt defining LLM behavior</param>
    /// <param name="userPrompt">User message to process</param>
    /// <returns>
    /// LLM response text with markdown code fences removed.
    /// On error, returns user-friendly error message from Morgana prompt configuration.
    /// </returns>
    public async Task<string> CompleteWithSystemPromptAsync(string conversationId, string systemPrompt, string userPrompt)
    {
        try
        {
            ChatResponse response = await chatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt)
                ],
                new ChatOptions
                {
                    ConversationId = conversationId
                });

            // Strip markdown code fences from JSON responses
            return response.Text
                .Replace("```json", string.Empty)
                .Replace("```", string.Empty);
        }
        catch (Exception ex)
        {
            // Return user-friendly error message from Morgana prompts
            List<Records.ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<Records.ErrorAnswer>>("ErrorAnswers");
            Records.ErrorAnswer? llmError = errorAnswers.FirstOrDefault(e => string.Equals(e.Name, "LLMServiceError", StringComparison.OrdinalIgnoreCase));

            return llmError?.Content.Replace("((llm_error))", ex.Message)
                          ?? $"LLM service error: {ex.Message}";
        }
    }
}