using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using Morgana.AI.Telemetry;

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
    /// Logger factory used to instrument the chat client pipeline (in particular,
    /// <see cref="OpenTelemetryChatClient"/> via <see cref="WrapWithTelemetry"/>). May be
    /// <c>null</c> in test scenarios; in that case the telemetry decorator is skipped.
    /// </summary>
    protected readonly ILoggerFactory? loggerFactory;

    /// <summary>
    /// Initializes MorganaLLM abstraction.
    /// Loads the Morgana framework prompt for error message templates.
    /// </summary>
    /// <param name="configuration">Application configuration for provider-specific settings</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="loggerFactory">
    /// Optional logger factory used to instrument the chat client with the MEAI OpenTelemetry
    /// decorator. Pass <c>null</c> to skip the decorator (test paths, unit tests).
    /// </param>
    public MorganaLLM(
        IConfiguration configuration,
        IPromptResolverService promptResolverService,
        ILoggerFactory? loggerFactory = null)
    {
        this.configuration = configuration;
        this.promptResolverService = promptResolverService;
        this.loggerFactory = loggerFactory;

        morganaPrompt = promptResolverService.ResolveAsync("Morgana").GetAwaiter().GetResult();
    }

    /// <summary>
    /// Wraps the supplied <paramref name="inner"/> chat client with the MEAI
    /// <see cref="OpenTelemetryChatClient"/> decorator so every request emits OTel spans and
    /// metrics under the standard <c>gen_ai.*</c> semantic conventions (input/output token
    /// counts, cache_read input tokens, model name, response latency, errors).
    /// </summary>
    /// <param name="inner">The chat client to wrap (typically the raw provider client, possibly
    /// already wrapped by a provider-specific decorator like Anthropic's no-prefill guard).</param>
    /// <returns>
    /// The instrumented chat client when <see cref="loggerFactory"/> is available and
    /// <c>Morgana:OpenTelemetry:Enabled</c> is true; otherwise <paramref name="inner"/>
    /// unchanged. Provider-agnostic — all four concrete providers go through this single hook.
    /// </returns>
    /// <remarks>
    /// <para>The activity source / meter name is fixed (<c>Morgana.AI.LLM</c>) so the OTel
    /// pipeline registration is centralised; per-provider differentiation comes from the
    /// <c>gen_ai.system</c> attribute that the MEAI decorator emits automatically.</para>
    /// <para><c>EnableSensitiveData</c> is read from <c>Morgana:OpenTelemetry:EnableSensitiveData</c>
    /// (default <c>false</c>): when true, the spans include the actual message contents — useful
    /// in dev/troubleshooting, off in production for privacy.</para>
    /// </remarks>
    protected IChatClient WrapWithTelemetry(IChatClient inner)
    {
        if (loggerFactory is null)
            return inner;
        if (!configuration.GetValue("Morgana:OpenTelemetry:Enabled", true))
            return inner;

        bool enableSensitiveData = configuration.GetValue("Morgana:OpenTelemetry:EnableSensitiveData", false);

        return new ChatClientBuilder(inner)
            .UseOpenTelemetry(loggerFactory, MorganaTelemetry.LLMChatClientSourceName,
                otel => otel.EnableSensitiveData = enableSensitiveData)
            .Build();
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