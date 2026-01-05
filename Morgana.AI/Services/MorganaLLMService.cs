using Anthropic;
using Anthropic.Core;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Interfaces;
using static Morgana.AI.Records;

namespace Morgana.AI.Services;

/// <summary>
/// Base implementation of ILLMService providing common LLM interaction patterns.
/// Supports multiple LLM providers (Anthropic, Azure OpenAI) via Microsoft.Extensions.AI abstraction.
/// </summary>
/// <remarks>
/// <para><strong>Architecture:</strong></para>
/// <code>
/// MorganaLLMService (abstract base)
///   ├── AnthropicService (Anthropic Claude models)
///   └── AzureOpenAIService (Azure OpenAI GPT models)
/// </code>
/// <para><strong>Provider Selection:</strong></para>
/// <para>The active implementation is selected in Program.cs based on configuration:</para>
/// <code>
/// builder.Services.AddSingleton&lt;ILLMService&gt;(sp => {
///     string provider = config["LLM:Provider"];
///     return provider.ToLowerInvariant() switch {
///         "anthropic" => new AnthropicService(config, promptResolver),
///         "azureopenai" => new AzureOpenAIService(config, promptResolver),
///         _ => throw new InvalidOperationException("Unsupported provider")
///     };
/// });
/// </code>
/// <para><strong>Key Features:</strong></para>
/// <list type="bullet">
/// <item><term>Conversation Management</term><description>Tracks conversation history per conversationId</description></item>
/// <item><term>Error Handling</term><description>Returns user-friendly error messages from Morgana prompts</description></item>
/// <item><term>JSON Cleanup</term><description>Strips markdown code fences from LLM responses</description></item>
/// <item><term>System Prompt Support</term><description>Explicit system prompt injection for actors</description></item>
/// </list>
/// </remarks>
public class MorganaLLMService : ILLMService
{
    protected readonly IConfiguration configuration;
    protected readonly IPromptResolverService promptResolverService;
    protected readonly Prompt morganaPrompt;
    
    /// <summary>
    /// Microsoft.Extensions.AI chat client for LLM interactions.
    /// Initialized by derived classes (AnthropicService, AzureOpenAIService).
    /// </summary>
    protected IChatClient chatClient;

    /// <summary>
    /// Initializes the base MorganaLLMService.
    /// Loads the Morgana framework prompt for error message templates.
    /// </summary>
    /// <param name="configuration">Application configuration for provider-specific settings</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    public MorganaLLMService(
        IConfiguration configuration,
        IPromptResolverService promptResolverService)
    {
        this.configuration = configuration;
        this.promptResolverService = promptResolverService;

        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets the underlying Microsoft.Extensions.AI chat client.
    /// Used by AgentAdapter to create AIAgent instances with tool calling support.
    /// </summary>
    /// <returns>IChatClient instance configured for the active provider</returns>
    public IChatClient GetChatClient() => chatClient;
    
    /// <summary>
    /// Gets the prompt resolver service associated with this LLM service.
    /// </summary>
    /// <returns>IPromptResolverService instance</returns>
    public IPromptResolverService GetPromptResolverService() => promptResolverService;

    /// <summary>
    /// Performs a completion with the Morgana system prompt and user message.
    /// Uses the base Morgana prompt as the system context.
    /// </summary>
    /// <param name="conversationId">Unique identifier of the conversation</param>
    /// <param name="prompt">User message to process</param>
    /// <returns>LLM response text</returns>
    /// <remarks>
    /// This method is a convenience wrapper around CompleteWithSystemPromptAsync
    /// using the Morgana framework prompt as the system context.
    /// </remarks>
    public async Task<string> CompleteAsync(string conversationId, string prompt)
        => await CompleteWithSystemPromptAsync(conversationId, morganaPrompt.Content, prompt);

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
    /// <remarks>
    /// <para><strong>Message Structure:</strong></para>
    /// <code>
    /// [
    ///   { "role": "system", "content": systemPrompt },
    ///   { "role": "user", "content": userPrompt }
    /// ]
    /// </code>
    /// <para><strong>JSON Cleanup:</strong></para>
    /// <para>LLMs sometimes wrap JSON responses in markdown code fences (```json ... ```).
    /// This method automatically strips these fences to enable reliable JSON parsing by actors.</para>
    /// <code>
    /// // LLM response
    /// ```json
    /// {"intent": "billing", "confidence": 0.95}
    /// ```
    /// 
    /// // After cleanup
    /// {"intent": "billing", "confidence": 0.95}
    /// </code>
    /// <para><strong>Error Handling:</strong></para>
    /// <para>On exception, returns the LLMServiceError template from Morgana prompts with
    /// the exception message injected. This ensures users receive friendly error messages
    /// rather than technical stack traces.</para>
    /// <code>
    /// // Configuration (morgana.json)
    /// "LLMServiceError": "I'm sorry, the magic sphere refused to cooperate: ((llm_error))"
    /// 
    /// // On error
    /// "I'm sorry, the magic sphere refused to cooperate: Rate limit exceeded"
    /// </code>
    /// </remarks>
    public async Task<string> CompleteWithSystemPromptAsync(string conversationId, string systemPrompt, string userPrompt)
    {
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, userPrompt)
        ];

        ChatOptions chatOptions = new ChatOptions
        {
            ConversationId = conversationId
        };

        try
        {
            ChatResponse response = await chatClient.GetResponseAsync(messages, chatOptions);
            
            // Strip markdown code fences from JSON responses
            return response.Text
                .Replace("```json", string.Empty)
                .Replace("```", string.Empty);
        }
        catch (Exception ex)
        {
            // Return user-friendly error message from Morgana prompts
            List<ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<ErrorAnswer>>("ErrorAnswers");
            ErrorAnswer? llmError = errorAnswers.FirstOrDefault(e => string.Equals(e.Name, "LLMServiceError", StringComparison.OrdinalIgnoreCase));
            
            return llmError?.Content.Replace("((llm_error))", ex.Message) 
                          ?? $"LLM service error: {ex.Message}";
        }
    }
}

/* Provider-Specific Implementations */

/// <summary>
/// Anthropic Claude implementation of ILLMService.
/// Supports Claude models (Claude Sonnet 4, etc.) via Anthropic SDK.
/// </summary>
/// <remarks>
/// <para><strong>Configuration (appsettings.json):</strong></para>
/// <code>
/// {
///   "LLM": {
///     "Provider": "anthropic",
///     "Anthropic": {
///       "ApiKey": "sk-ant-...",
///       "Model": "claude-sonnet-4-5"
///     }
///   }
/// }
/// </code>
/// <para><strong>Supported Models (according to Anthropic documentation, up to date):</strong></para>
/// <list type="bullet">
/// <item>claude-sonnet-4-5 (Claude Sonnet 4.5 - latest)</item>
/// <item>claude-opus-4-5 (Claude Opus 4.5 - most capable)</item>
/// <item>claude-haiku-4-5 (Claude Haiku 4.5 - fastest)</item>
/// </list>
/// <para><strong>Features:</strong></para>
/// <list type="bullet">
/// <item>Extended context window (200K+ tokens)</item>
/// <item>Tool calling support via Microsoft.Extensions.AI abstraction</item>
/// <item>Streaming support (not currently used by Morgana)</item>
/// </list>
/// </remarks>
public class AnthropicService : MorganaLLMService
{
    /// <summary>
    /// Initializes a new instance of AnthropicService.
    /// Creates Anthropic client and wraps it with Microsoft.Extensions.AI IChatClient.
    /// </summary>
    /// <param name="configuration">Application configuration containing Anthropic API key and model</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    public AnthropicService(
        IConfiguration configuration,
        IPromptResolverService promptResolverService) : base(configuration, promptResolverService)
    {
        AnthropicClient anthropicClient = new AnthropicClient(
            new ClientOptions
            {
                APIKey = this.configuration["LLM:Anthropic:ApiKey"]!
            });
        string anthropicModel = this.configuration["LLM:Anthropic:Model"]!;

        // Wrap Anthropic client with Microsoft.Extensions.AI abstraction
        chatClient = anthropicClient.AsIChatClient(anthropicModel);
    }
}

/// <summary>
/// Azure OpenAI implementation of ILLMService.
/// Supports GPT models via Azure OpenAI Service.
/// </summary>
/// <remarks>
/// <para><strong>Configuration (appsettings.json):</strong></para>
/// <code>
/// {
///   "LLM": {
///     "Provider": "azureopenai",
///     "AzureOpenAI": {
///       "Endpoint": "https://your-resource.openai.azure.com/",
///       "ApiKey": "your-api-key",
///       "DeploymentName": "gpt-4"
///     }
///   }
/// }
/// </code>
/// <para><strong>Supported Models (according to Microsoft/OpenAI documentation, up to date):</strong></para>
/// <list type="bullet">
/// <item>GPT-4 Turbo (gpt-4, gpt-4-turbo)</item>
/// <item>GPT-3.5 Turbo (gpt-35-turbo)</item>
/// </list>
/// <para><strong>Deployment Notes:</strong></para>
/// <para>Azure OpenAI requires pre-deployed models in your Azure resource.
/// The DeploymentName must match your Azure deployment, not the base model name.</para>
/// <para><strong>Features:</strong></para>
/// <list type="bullet">
/// <item>Enterprise-grade security and compliance</item>
/// <item>Tool calling support via Microsoft.Extensions.AI abstraction</item>
/// <item>Content filtering and safety features</item>
/// <item>Regional deployment options</item>
/// </list>
/// </remarks>
public class AzureOpenAIService : MorganaLLMService
{
    /// <summary>
    /// Initializes a new instance of AzureOpenAIService.
    /// Creates Azure OpenAI client and wraps it with Microsoft.Extensions.AI IChatClient.
    /// </summary>
    /// <param name="configuration">Application configuration containing Azure endpoint, key, and deployment</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    public AzureOpenAIService(
        IConfiguration configuration,
        IPromptResolverService promptResolverService) : base(configuration, promptResolverService)
    {
        AzureOpenAIClient azureClient = new AzureOpenAIClient(
            new Uri(this.configuration["LLM:AzureOpenAI:Endpoint"]!),
            new AzureKeyCredential(this.configuration["LLM:AzureOpenAI:ApiKey"]!));
        string deploymentName = this.configuration["LLM:AzureOpenAI:DeploymentName"]!;

        // Get chat client for specific deployment and wrap with Microsoft.Extensions.AI abstraction
        chatClient = azureClient.GetChatClient(deploymentName).AsIChatClient();
    }
}