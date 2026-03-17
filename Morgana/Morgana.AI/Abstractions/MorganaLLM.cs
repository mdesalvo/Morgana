using System.ClientModel;
using Anthropic;
using Anthropic.Core;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Interfaces;
using OpenAI;

namespace Morgana.AI.Abstractions;

/// <summary>
/// Base implementation of ILLMService providing common LLM interaction patterns.
/// Supports multiple LLM providers (Anthropic, Azure OpenAI, OpenAI) via Microsoft.Extensions.AI abstraction.
/// </summary>
/// <remarks>
/// <para><strong>Architecture:</strong></para>
/// <code>
/// MorganaLLM (abstract base)
///   ├── Anthropic (Anthropic Claude models)
///   ├── AzureOpenAI (Azure OpenAI GPT models)
///   └── OpenAI (OpenAI GPT models)
/// </code>
/// <para><strong>Provider Selection:</strong></para>
/// <para>The active implementation is selected in Program.cs based on configuration:</para>
/// <code>
/// builder.Services.AddSingleton&lt;ILLMService&gt;(sp => {
///     string provider = config["Morgana:LLM:Provider"];
///     return provider.ToLowerInvariant() switch {
///         "anthropic"   => new Anthropic(config, promptResolver),
///         "azureopenai" => new AzureOpenAI(config, promptResolver),
///         "openai" => new OpenAI(config, promptResolver),
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
public class MorganaLLM : ILLMService
{
    protected readonly IConfiguration configuration;
    protected readonly IPromptResolverService promptResolverService;
    protected readonly Records.Prompt morganaPrompt;

    /// <summary>
    /// Microsoft.Extensions.AI chat client for LLM interactions.
    /// Initialized by derived classes (Anthropic, AzureOpenAI).
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
        => await CompleteWithSystemPromptAsync(conversationId, morganaPrompt.Target, prompt);

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
            List<Records.ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<Records.ErrorAnswer>>("ErrorAnswers");
            Records.ErrorAnswer? llmError = errorAnswers.FirstOrDefault(e => string.Equals(e.Name, "LLMServiceError", StringComparison.OrdinalIgnoreCase));

            return llmError?.Content.Replace("((llm_error))", ex.Message)
                          ?? $"LLM service error: {ex.Message}";
        }
    }
}

/* Provider-Specific Implementations */

/// <summary>
/// Anthropic implementation of ILLMService
/// Supports Claude models (claude-opus-4-5, claude-sonnet-4-5, ...)
/// </summary>
/// <remarks>
/// <para><strong>Configuration (appsettings.json):</strong></para>
/// <code>
/// {
///   "Morgana": {
///     "LLM": {
///       "Provider": "anthropic",
///       "Anthropic": {
///         "ApiKey": "sk-ant-...",
///         "Model": "claude-sonnet-4-5"
///       }
///     }
///   }
/// }
/// </code>
/// </remarks>
public class Anthropic : MorganaLLM
{
    /// <summary>
    /// Initializes a new instance of Anthropic.
    /// Creates Anthropic client and wraps it with Microsoft.Extensions.AI IChatClient.
    /// </summary>
    /// <param name="configuration">Application configuration containing Anthropic API key and model</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    public Anthropic(
        IConfiguration configuration,
        IPromptResolverService promptResolverService) : base(configuration, promptResolverService)
    {
        AnthropicClient anthropicClient = new AnthropicClient(
            new ClientOptions
            {
                ApiKey = this.configuration["Morgana:LLM:Anthropic:ApiKey"]!
            });
        string anthropicModel = this.configuration["Morgana:LLM:Anthropic:Model"]!;

        // Wrap Anthropic client with Microsoft.Extensions.AI abstraction
        chatClient = anthropicClient.AsIChatClient(anthropicModel);
    }
}

/// <summary>
/// Azure OpenAI implementation of ILLMService
/// Supports GPT models via Azure OpenAI Service
/// </summary>
/// <remarks>
/// <para><strong>Configuration (appsettings.json):</strong></para>
/// <code>
/// {
///   "Morgana: {
///     "LLM": {
///       "Provider": "azureopenai",
///       "AzureOpenAI": {
///         "Endpoint": "https://your-resource.openai.azure.com/",
///         "ApiKey": "your-api-key",
///         "DeploymentName": "your-deployment-name"
///       }
///     }
///   }
/// }
/// </code>
/// <para><strong>Deployment Notes:</strong></para>
/// <para>Azure OpenAI requires pre-deployed models in your Azure resource.
/// The DeploymentName must match your Azure deployment, not the base model name.</para>
/// </remarks>
public class AzureOpenAI : MorganaLLM
{
    /// <summary>
    /// Initializes a new instance of AzureOpenAI.
    /// Creates Azure OpenAI client and wraps it with Microsoft.Extensions.AI IChatClient.
    /// </summary>
    /// <param name="configuration">Application configuration containing Azure endpoint, key, and deployment</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    public AzureOpenAI(
        IConfiguration configuration,
        IPromptResolverService promptResolverService) : base(configuration, promptResolverService)
    {
        AzureOpenAIClient azureClient = new AzureOpenAIClient(
            new Uri(this.configuration["Morgana:LLM:AzureOpenAI:Endpoint"]!),
            new AzureKeyCredential(this.configuration["Morgana:LLM:AzureOpenAI:ApiKey"]!));
        string deploymentName = this.configuration["Morgana:LLM:AzureOpenAI:DeploymentName"]!;

        // Get chat client for specific deployment and wrap with Microsoft.Extensions.AI abstraction
        chatClient = azureClient.GetChatClient(deploymentName).AsIChatClient();
    }
}

/// <summary>
/// OpenAI implementation of ILLMService
/// Supports GPT models via OpenAI Service (gpt-4o, gpt-4o-mini, ...)
/// </summary>
/// <remarks>
/// <para><strong>Configuration (appsettings.json):</strong></para>
/// <code>
/// {
///   "Morgana: {
///     "LLM": {
///       "Provider": "openai",
///       "OpenAI": {
///         "ApiKey": "your-api-key",
///         "Model": "gpt-4o"
///       }
///     }
///   }
/// }
/// </code>
/// </remarks>
public class OpenAI : MorganaLLM
{
    /// <summary>
    /// Initializes a new instance of OpenAI.
    /// Creates OpenAI client and wraps it with Microsoft.Extensions.AI IChatClient.
    /// </summary>
    /// <param name="configuration">Application configuration containing Azure endpoint, key, and deployment</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    public OpenAI(
        IConfiguration configuration,
        IPromptResolverService promptResolverService) : base(configuration, promptResolverService)
    {
        OpenAIClient openaiClient = new OpenAIClient(
            new ApiKeyCredential(this.configuration["Morgana:LLM:OpenAI:ApiKey"]!));
        string model = this.configuration["Morgana:LLM:OpenAI:Model"]!;

        // Get chat client for specific deployment and wrap with Microsoft.Extensions.AI abstraction
        chatClient = openaiClient.GetChatClient(model).AsIChatClient();
    }
}