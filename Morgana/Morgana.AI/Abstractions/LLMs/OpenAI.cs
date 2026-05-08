using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI.Interfaces;
using OpenAI;

namespace Morgana.AI.Abstractions.LLMs;

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
