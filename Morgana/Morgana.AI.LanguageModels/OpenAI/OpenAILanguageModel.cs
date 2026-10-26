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
/// Requires ApiKey on the tier's connection. The embedded pricing.json carries OpenAI's standard processing prices for
/// prompts up to 272,000 tokens: its cache write is the 30-minute write where OpenAI bills one and plain input elsewhere.
/// </remarks>
/// <param name="loggerFactory">Optional logger factory for the <see cref="AttemptLoggingHandler"/> that writes one line per attempt on the wire; <c>null</c> writes nothing.</param>
public class OpenAILanguageModel(ILoggerFactory? loggerFactory = null) : MorganaLanguageModel<OpenAITierConnection>
{
    /// <inheritdoc />
    // The name a tier writes in its Provider key: it must stay stable whatever the class is called.
    public override string Provider => "OpenAI";

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
