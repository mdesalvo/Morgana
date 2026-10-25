using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using OpenAI;

namespace Morgana.AI.LanguageModels.OpenAI;

/// <summary>
/// OpenAI provider.<br/>
/// Supports GPT models via the OpenAI Responses API (gpt-6-luna, gpt-6-sol, gpt-6-astra, ...)
/// </summary>
/// <remarks>
/// Requires ApiKey on the tier's connection.
/// </remarks>
/// <param name="loggerFactory">Optional logger factory for the <see cref="AttemptLoggingHandler"/> that writes one line per attempt on the wire; <c>null</c> writes nothing.</param>
public class OpenAILanguageModel(ILoggerFactory? loggerFactory = null) : MorganaLanguageModel<OpenAITierConnection>
{
    /// <inheritdoc />
    // The name a tier writes in its Provider key: it must stay stable whatever the class is called.
    public override string Provider => "OpenAI";

    /// <inheritdoc />
    public override bool IsContentFilterRefusal(Exception exception) =>
        // No refusal of this provider is known to reach the framework as an exception.
        false;

    /// <summary>What each model of this provider costs in US dollars per million tokens.</summary>
    // OpenAI's standard processing prices for prompts up to 272,000 tokens.
    // The cache write is the 30-minute write where OpenAI bills one and plain input elsewhere.
    private static readonly Dictionary<string, Records.ModelPricing> ModelPrices = new Dictionary<string, Records.ModelPricing>(StringComparer.Ordinal)
    {
        // GPT-6.X
        ["gpt-6.1-sol"]   = new Records.ModelPricing(2.00m, 10.00m, 0.10m, 2.50m),
        ["gpt-6-astra"]   = new Records.ModelPricing(10.00m, 50.00m, 1.00m, 12.50m),
        ["gpt-6-sol"]     = new Records.ModelPricing(2.00m, 10.00m, 0.20m, 2.50m),
        ["gpt-6-luna"]    = new Records.ModelPricing(0.10m, 0.50m, 0.01m, 0.125m),
        // GPT-5.6
        ["gpt-5.6-sol"]   = new Records.ModelPricing(4.00m, 20.00m, 0.40m, 5.00m),
        ["gpt-5.6-terra"] = new Records.ModelPricing(2.00m, 12.00m, 0.20m, 2.50m),
        ["gpt-5.6-luna"]  = new Records.ModelPricing(0.20m, 1.20m, 0.02m, 0.25m),
        // GPT-5.X
        ["gpt-5.5"]       = new Records.ModelPricing(5.00m, 30.00m, 0.50m, 5.00m),
        ["gpt-5.4"]       = new Records.ModelPricing(2.50m, 15.00m, 0.25m, 2.50m),
        ["gpt-5.4-mini"]  = new Records.ModelPricing(0.75m, 4.50m, 0.075m, 0.75m),
        ["gpt-5.4-nano"]  = new Records.ModelPricing(0.20m, 1.25m, 0.02m, 0.20m),
        ["gpt-5.2"]       = new Records.ModelPricing(1.75m, 14.00m, 0.175m, 1.75m),
        ["gpt-5.1"]       = new Records.ModelPricing(1.25m, 10.00m, 0.125m, 1.25m),
        ["gpt-5"]         = new Records.ModelPricing(1.25m, 10.00m, 0.125m, 1.25m),
        ["gpt-5-mini"]    = new Records.ModelPricing(0.25m, 2.00m, 0.025m, 0.25m),
        ["gpt-5-nano"]    = new Records.ModelPricing(0.05m, 0.40m, 0.005m, 0.05m),
        // GPT-4.X
        ["gpt-4.1"]       = new Records.ModelPricing(2.00m, 8.00m, 0.50m, 2.00m),
        ["gpt-4.1-mini"]  = new Records.ModelPricing(0.40m, 1.60m, 0.10m, 0.40m),
        ["gpt-4.1-nano"]  = new Records.ModelPricing(0.10m, 0.40m, 0.025m, 0.10m),
        ["gpt-4o"]        = new Records.ModelPricing(2.50m, 10.00m, 1.25m, 2.50m),
        ["gpt-4o-mini"]   = new Records.ModelPricing(0.15m, 0.60m, 0.075m, 0.15m),
        // O-SERIES
        ["o3"]            = new Records.ModelPricing(2.00m, 8.00m, 0.50m, 2.00m),
        ["o4-mini"]       = new Records.ModelPricing(1.10m, 4.40m, 0.275m, 1.10m),
    };

    /// <inheritdoc />
    protected override Records.ModelPricing? GetModelPricing(string modelId) =>
        // A model absent from the table is one that this provider does not price.
        ModelPrices.GetValueOrDefault(modelId);

    /// <inheritdoc />
    protected override void ValidateConnection(OpenAITierConnection connection) =>
        RequireField(connection.ApiKey, nameof(OpenAITierConnection.ApiKey));

    /// <inheritdoc />
    protected override IChatClient CreateChatClient(OpenAITierConnection connection, Records.TierConfiguration options)
    {
        // A throttled call is retried a bounded number of times. NetworkTimeout bounds the wait for the
        // headers and each wait between two reads of the body; a streamed body that it cuts is not retried.
        OpenAIClientOptions clientOptions = new OpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(connection.MaxRetries),
            NetworkTimeout = TimeSpan.FromSeconds(connection.TimeoutSeconds),
            // The pipeline's NetworkTimeout still bounds each attempt, so the HTTP client sets none of its own (its default 100 s would cut first).
            Transport = new HttpClientPipelineTransport(
                AttemptLoggingHandler.CreateHttpClient(loggerFactory?.CreateLogger<OpenAILanguageModel>(), Provider, Timeout.InfiniteTimeSpan))
        };

        // The key is required by ValidateConnection, so it is present when a client is built.
        OpenAIClient openAIClient = new OpenAIClient(new ApiKeyCredential(connection.ApiKey!), clientOptions);

        // The decorator is what the framework talks to: the Responses API is the one that gpt-6-sol and gpt-6-luna serve function calling on
        // with reasoning enabled and the only one whose usage reports the cache writes, which OpenAI bills apart.
        // The SDK marks the Responses client as evaluation-only.
#pragma warning disable OPENAI001
        return new MorganaOpenAIClient(openAIClient.GetResponsesClient().AsIChatClient(options.ModelId));
#pragma warning restore OPENAI001
    }
}

/// <summary>How a tier reaches OpenAI.</summary>
/// <param name="ApiKey">Credential of the OpenAI account.</param>
/// <param name="MaxRetries">Retries of a refused call.</param>
/// <param name="TimeoutSeconds">Ceiling of one attempt's wait for the response headers and of each wait between two reads of its body; a streamed body cut by it is not retried.</param>
public record OpenAITierConnection(
    string? ApiKey = null,
    int MaxRetries = 2,
    int TimeoutSeconds = 60);
