using System.ClientModel;
using Anthropic;
using Anthropic.Core;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Interfaces;
using OllamaSharp;
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
///   ├── Ollama (Ollama local models)
///   └── OpenAI (OpenAI GPT models)
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

/* Provider-Specific Implementations */

/// <summary>
/// Anthropic implementation of ILLMService.<br/>
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
/// Azure OpenAI implementation of ILLMService.<br/>
/// Supports GPT models via Azure OpenAI Service (gpt-4o, ...)
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
/// Ollama implementation of ILLMService.<br/>
/// Supports local models via OllamaSharp (qwen2.5:14b-instruct-q4_K_M, ...).
/// </summary>
/// <remarks>
/// <para><strong>Configuration (appsettings.json):</strong></para>
/// <code>
/// {
///   "Morgana": {
///     "LLM": {
///       "Provider": "ollama",
///       "Ollama": {
///         "Endpoint": "http://localhost:11434/",
///         "Model": "your-ollama-model" //e.g: qwen2.5:14b-instruct-q4_K_M, ...
///         "TimeoutSeconds": 180
///       }
///     }
///   }
/// }
/// </code>
/// <para><strong>Important Notes:</strong></para>
/// <para>- Morgana is an AI orchestrator which relies heavily on tool calling (context variables, quick replies, rich cards).
/// Choose a model with solid function calling support for best results (e.g: qwen2.5:14b-instruct-q4_K_M).</para>
/// <para>- Before starting Morgana, check with "ollama ps" that your model is already loaded into memory!</para>
/// </remarks>
public class Ollama : MorganaLLM
{
    /// <summary>
    /// Initializes a new instance of Ollama.
    /// Creates Ollama client and wraps it with Microsoft.Extensions.AI IChatClient.
    /// </summary>
    /// <param name="configuration">Application configuration containing Ollama endpoint and model</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    public Ollama(
        IConfiguration configuration,
        IPromptResolverService promptResolverService) : base(configuration, promptResolverService)
    {
        // Get chat client for specific Ollama model (it is already compatible with Microsoft.Extensions.AI abstractions)
        chatClient = new OllamaApiClient(
            new HttpClient
            {
                BaseAddress = new Uri(this.configuration["Morgana:LLM:Ollama:Endpoint"]!),
                Timeout = TimeSpan.FromSeconds(Convert.ToInt32(this.configuration["Morgana:LLM:Ollama:TimeoutSeconds"]))
            }, this.configuration["Morgana:LLM:Ollama:Model"]!);
    }
}

/// <summary>
/// OpenAI implementation of ILLMService.<br/>
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
///         "Model": "your-openai-model" //e.g: gpt-4o
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
    /// <param name="configuration">Application configuration containing OpenAI key and model</param>
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