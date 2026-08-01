using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using OpenAI;

namespace Morgana.AI.Abstractions.LLMs;

/// <summary>
/// OpenAI implementation of ILLMService.<br/>
/// Supports GPT models via OpenAI Service (gpt-4o, gpt-4o-mini, ...)
/// </summary>
/// <remarks>
/// OpenAI provider implementation. Configuration under Morgana:LLM:OpenAI with ApiKey and Tiers map
/// (Efficiency/Performance). MagicDust pricing: Efficiency tier matches gpt-4o-mini's real per-token cost exactly;
/// Performance tier is floored (gpt-4o costs ~17× more per token, too steep for per-conversation budget,
/// so it's reduced proportionally). When you change either tier's ModelId, recalibrate InputTokensPerDustUnit
/// and OutputTokensPerDustUnit for that model's actual pricing.
/// </remarks>
public class OpenAI : MorganaLLM
{
    /// <summary>
    /// Initializes a new instance of OpenAI.
    /// Creates OpenAI client and wraps it with Microsoft.Extensions.AI IChatClient.
    /// </summary>
    /// <param name="configuration">Application configuration containing OpenAI key and model</param>
    /// <param name="promptResolverService">Service for resolving prompt templates</param>
    /// <param name="loggerFactory">Optional logger factory used to instrument the chat client with the MEAI OpenTelemetry decorator.</param>
    public OpenAI(
        IConfiguration configuration,
        IPromptResolverService promptResolverService,
        ILoggerFactory? loggerFactory = null) : base(configuration, promptResolverService, loggerFactory)
    {
        OpenAIClient openaiClient = new OpenAIClient(
            new ApiKeyCredential(this.configuration["Morgana:LLM:OpenAI:ApiKey"]!));

        // Binds the tiers declared in configuration so they're available at runtime for
        // matching against each agent's declared tier (see Records.TierDefinition remarks
        // for how the config layout is structured).
        Dictionary<Records.LLMTier, Records.TierDefinition> tiers =
            this.configuration.GetSection("Morgana:LLM:OpenAI:Tiers").Get<Dictionary<Records.LLMTier, Records.TierDefinition>>() ?? [];

        // One chat client per configured tier, sharing the same OpenAIClient (API key only),
        // wrapped with the MEAI OpenTelemetry decorator for gen_ai.* spans and metrics.
        foreach ((Records.LLMTier tier, Records.TierDefinition tierDefinition) in tiers)
            RegisterTierClient(tier, tierDefinition.Options.ModelId, WrapWithTelemetry(openaiClient.GetChatClient(tierDefinition.Options.ModelId).AsIChatClient()), tierDefinition.MagicDust, tierDefinition.Options.ToChatOptions());

        // Wraps up tier registration and picks which client the framework's own actors
        // (Guard, Classifier, Presenter, ChannelAdapter) will use.

        FinalizeModelRegistration();
    }
}