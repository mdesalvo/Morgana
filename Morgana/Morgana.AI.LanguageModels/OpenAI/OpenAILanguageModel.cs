using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using OpenAI;

namespace Morgana.AI.LanguageModels.OpenAI;

/// <summary>
/// The OpenAI provider, serving GPT models such as gpt-6-luna, gpt-6-sol and gpt-6-astra through the Responses API on a tier's ApiKey.
/// Its models.json carries the standard prices for prompts up to 272,000 tokens, the cache write being the 30-minute write where OpenAI bills one and plain input elsewhere.
/// </summary>
/// <param name="loggerFactory">Writes the attempt lines of <see cref="AttemptLoggingHandler"/>; <c>null</c> writes nothing.</param>
public class OpenAILanguageModel(ILoggerFactory? loggerFactory = null) : MorganaLanguageModel<OpenAITierConnection>
{
    /// <summary>The name a tier writes in its Provider key, which stays the same whatever the class is called.</summary>
    public override string Provider => "OpenAI";

    /// <inheritdoc />
    protected override void ValidateConnection(OpenAITierConnection connection) =>
        RequireField(connection.ApiKey, nameof(OpenAITierConnection.ApiKey));

    /// <inheritdoc />
    protected override IChatClient CreateChatClient(OpenAITierConnection connection, Records.ModelOptions options)
    {
        // A throttled call is retried a bounded number of times. NetworkTimeout bounds the wait for the
        // headers and each wait between two reads of the body; a streamed body that it cuts is not retried.
        // The pipeline's NetworkTimeout bounds each attempt, so the HTTP client that writes the attempt lines
        // sets none of its own: its default 100 seconds would cut first.
        OpenAIClientOptions clientOptions = new OpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(connection.MaxRetries),
            NetworkTimeout = TimeSpan.FromSeconds(connection.TimeoutSeconds),
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

/// <summary>How a tier reaches OpenAI, bound from the tier's Connection with the shared Reliability laid under it.</summary>
/// <param name="ApiKey">Credential of the OpenAI account.</param>
/// <param name="MaxRetries">Retries of a refused call.</param>
/// <param name="TimeoutSeconds">How long one attempt waits for the response headers and between two reads of its body; a streamed body cut by it is not retried.</param>
public record OpenAITierConnection(
    string? ApiKey = null,
    int MaxRetries = 2,
    int TimeoutSeconds = 60);