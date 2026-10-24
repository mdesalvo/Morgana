using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;

namespace Morgana.AI.LanguageModels.Anthropic;

/// <summary>
/// Anthropic provider.<br/>
/// Supports Claude models (claude-haiku-5-5, claude-sonnet-5-5, claude-opus-5-5, ...)
/// </summary>
/// <remarks>
/// Requires ApiKey on the tier's connection.
/// </remarks>
/// <param name="loggerFactory">
/// Optional logger factory used by <see cref="MorganaAnthropicClient"/> and by the <see cref="AttemptLoggingHandler"/> that writes one line per attempt on the wire. When <c>null</c>, the guard's
/// diagnostic channel is silent but the message-list normalization still applies.
/// </param>
public class AnthropicLanguageModel(ILoggerFactory? loggerFactory = null) : MorganaLanguageModel<AnthropicTierConnection>
{
    /// <inheritdoc />
    // The name a tier writes in its Provider key: it must stay stable whatever the class is called.
    public override string Provider => "Anthropic";

    /// <inheritdoc />
    public override bool IsContentFilterRefusal(Exception exception) =>
        // No refusal of this provider is known to reach the framework as an exception.
        false;

    /// <summary>What each model of this provider costs in US dollars per million tokens.</summary>
    // Anthropic's standard processing prices. The cache write is the 1-hour write because MorganaAnthropicClient asks for a cache that lives an hour.
    // Claude Haiku 5.5 is priced at its band for prompts up to 100,000 tokens.
    private static readonly Dictionary<string, Records.ModelPricing> ModelPrices = new Dictionary<string, Records.ModelPricing>(StringComparer.Ordinal)
    {
        // FABLE
        ["claude-fable-5-1"] = new Records.ModelPricing(10.00m, 50.00m, 0.25m, 20.00m),
        ["claude-fable-5"]   = new Records.ModelPricing(10.00m, 50.00m, 1.00m, 20.00m),
        // OPUS
        ["claude-opus-5-5"] = new Records.ModelPricing(4.00m, 20.00m, 0.20m, 8.00m),
        ["claude-opus-5"]   = new Records.ModelPricing(5.00m, 25.00m, 0.50m, 10.00m),
        ["claude-opus-4-8"] = new Records.ModelPricing(5.00m, 25.00m, 0.50m, 10.00m),
        ["claude-opus-4-7"] = new Records.ModelPricing(5.00m, 25.00m, 0.50m, 10.00m),
        ["claude-opus-4-6"] = new Records.ModelPricing(5.00m, 25.00m, 0.50m, 10.00m),
        ["claude-opus-4-5"] = new Records.ModelPricing(5.00m, 25.00m, 0.50m, 10.00m),
        // SONNET
        ["claude-sonnet-5-5"] = new Records.ModelPricing(2.00m, 10.00m, 0.10m, 4.00m),
        ["claude-sonnet-5"]   = new Records.ModelPricing(2.00m, 10.00m, 0.20m, 4.00m),
        ["claude-sonnet-4-6"] = new Records.ModelPricing(3.00m, 15.00m, 0.30m, 6.00m),
        ["claude-sonnet-4-5"] = new Records.ModelPricing(3.00m, 15.00m, 0.30m, 6.00m),
        // HAIKU
        ["claude-haiku-5-5"] = new Records.ModelPricing(0.10m, 0.50m, 0.01m, 0.20m),
        ["claude-haiku-4-5"] = new Records.ModelPricing(1.00m, 5.00m, 0.10m, 2.00m)
    };

    /// <inheritdoc />
    protected override Records.ModelPricing? GetModelPricing(string modelId) =>
        // A model absent from the table is one that this provider does not price.
        ModelPrices.GetValueOrDefault(modelId);

    /// <inheritdoc />
    protected override void ValidateConnection(AnthropicTierConnection connection) =>
        RequireField(connection.ApiKey, nameof(AnthropicTierConnection.ApiKey));

    /// <inheritdoc />
    /// <remarks>
    /// The SDK adapter is wrapped in <see cref="MorganaAnthropicClient"/>, which ends every request on a role
    /// that Claude accepts and marks the leading system message for prompt caching.
    /// </remarks>
    protected override IChatClient CreateChatClient(AnthropicTierConnection connection, Records.TierConfiguration options)
    {
        // Left to its defaults the SDK retries a rate-limited or overloaded call on its own, with a
        // backoff nobody sees. The retry ceiling and the per-attempt timeout bound a throttled call and
        // the handler writes down every attempt. The timeout bounds one attempt's wait for the response
        // headers only: a body that stalls after them, streamed or not, is bounded by no SDK setting
        // and only the caller's own silence budget ends it.
        AnthropicClient anthropicClient = new AnthropicClient(
            new ClientOptions
            {
                ApiKey = connection.ApiKey!,
                MaxRetries = connection.MaxRetries,
                Timeout = TimeSpan.FromSeconds(connection.TimeoutSeconds),
                Handlers = [new AttemptLoggingHandler(loggerFactory?.CreateLogger<AnthropicLanguageModel>(), Provider)]
            });

        // The decorator is what the framework talks to: it keeps requests inside Claude's API rules and marks the system prompt for caching.
        return new MorganaAnthropicClient(anthropicClient.AsIChatClient(options.ModelId), loggerFactory);
    }
}

/// <summary>How a tier reaches Anthropic.</summary>
/// <param name="ApiKey">Credential of the Anthropic account.</param>
/// <param name="MaxRetries">Retries of a refused call.</param>
/// <param name="TimeoutSeconds">Ceiling of one attempt's wait for the response headers; the body that follows is not bounded by it.</param>
public record AnthropicTierConnection(
    string? ApiKey = null,
    int MaxRetries = 2,
    int TimeoutSeconds = 60);
