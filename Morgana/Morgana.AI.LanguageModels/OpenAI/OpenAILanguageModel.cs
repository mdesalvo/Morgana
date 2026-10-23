using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using OpenAI;

namespace Morgana.AI.LanguageModels.OpenAI;

/// <summary>
/// OpenAI provider.<br/>
/// Supports GPT models via OpenAI Service (gpt-4o, gpt-4o-mini, ...)
/// </summary>
/// <remarks>
/// Requires ApiKey on the tier's connection. When you change a tier's ModelId, recalibrate
/// InputTokensPerDustUnit and OutputTokensPerDustUnit for that model's actual pricing.
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
        return new OpenAIClient(new ApiKeyCredential(connection.ApiKey!), clientOptions)
            .GetChatClient(options.ModelId)
            .AsIChatClient();
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
