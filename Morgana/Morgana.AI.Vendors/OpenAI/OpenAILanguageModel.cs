using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Morgana.AI.Abstractions;
using OpenAI;

namespace Morgana.AI.Vendors.OpenAI;

/// <summary>
/// OpenAI provider.<br/>
/// Supports GPT models via OpenAI Service (gpt-4o, gpt-4o-mini, ...)
/// </summary>
/// <remarks>
/// Requires ApiKey on the tier's connection. When you change a tier's ModelId, recalibrate
/// InputTokensPerDustUnit and OutputTokensPerDustUnit for that model's actual pricing.
/// </remarks>
public class OpenAILanguageModel : MorganaLanguageModel<OpenAITierConnection>
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

/// <summary>How a tier reaches OpenAI.</summary>
/// <param name="ApiKey">Credential of the OpenAI account.</param>
/// <param name="MaxRetries">Retries of a refused call.</param>
/// <param name="TimeoutSeconds">Ceiling of one attempt.</param>
public record OpenAITierConnection(
    string? ApiKey = null,
    int MaxRetries = 2,
    int TimeoutSeconds = 60);
