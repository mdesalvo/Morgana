using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Morgana.AI.Abstractions;
using OpenAI;

namespace Morgana.AI.LanguageModels;

/// <summary>
/// OpenAI provider.<br/>
/// Supports GPT models via OpenAI Service (gpt-4o, gpt-4o-mini, ...)
/// </summary>
/// <remarks>
/// Requires ApiKey on the tier's connection. When you change a tier's ModelId, recalibrate
/// InputTokensPerDustUnit and OutputTokensPerDustUnit for that model's actual pricing.
/// </remarks>
public class OpenAI : MorganaLanguageModel
{
    /// <inheritdoc />
    public override void ValidateConnection(Records.TierConnection connection) =>
        RequireField(connection.ApiKey, nameof(Records.TierConnection.ApiKey));

    /// <inheritdoc />
    public override IChatClient CreateChatClient(Records.TierConnection connection, Records.TierConfiguration options)
    {
        // A throttled call is retried a bounded number of times and one attempt is bounded in time,
        // so the ceiling of a call is the timeout times the retries.
        OpenAIClientOptions clientOptions = new OpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(connection.MaxRetries),
            NetworkTimeout = TimeSpan.FromSeconds(connection.TimeoutSeconds)
        };

        // The key is required by ValidateConnection, so it is present when a client is built.
        return new OpenAIClient(new ApiKeyCredential(connection.ApiKey!), clientOptions)
            .GetChatClient(options.ModelId)
            .AsIChatClient();
    }
}
