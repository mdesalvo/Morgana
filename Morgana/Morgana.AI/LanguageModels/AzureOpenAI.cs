using System.ClientModel;
using System.ClientModel.Primitives;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Morgana.AI.Abstractions;
using OpenAI;

namespace Morgana.AI.LanguageModels;

/// <summary>
/// Azure OpenAI provider.<br/>
/// Supports GPT models deployed via classic Azure OpenAI Service (gpt-4o, ...) as well as
/// Azure AI Foundry projects exposing the unified OpenAI-compatible v1 API (gpt-5.x, ...)
/// </summary>
/// <remarks>
/// Requires ApiKey and Endpoint on the tier's connection. Supports both classic Azure OpenAI endpoints (e.g., https://resource.openai.azure.com)
/// and Azure AI Foundry v1 API endpoints (e.g., https://resource.services.ai.azure.com/api/projects/X/openai/v1).
/// When you change a tier's ModelId to a different model, recalibrate InputTokensPerDustUnit and OutputTokensPerDustUnit.
/// </remarks>
public class AzureOpenAI : MorganaLanguageModel
{
    /// <inheritdoc />
    public override void ValidateConnection(Records.TierConnection connection)
    {
        RequireField(connection.ApiKey, nameof(Records.TierConnection.ApiKey));
        RequireField(connection.Endpoint, nameof(Records.TierConnection.Endpoint));
    }

    /// <inheritdoc />
    public override IChatClient CreateChatClient(Records.TierConnection connection, Records.TierConfiguration options)
    {
        Uri endpoint = new Uri(connection.Endpoint!);

        // Azure AI Foundry projects expose an OpenAI-compatible unified "v1" API surface
        // (path containing "/openai/v1") that rejects the "api-version" query parameter that
        // AzureOpenAIClient always appends. For these endpoints, the vanilla OpenAI client
        // (pointed at the Foundry endpoint) must be used instead.
        bool isFoundryV1 = endpoint.AbsolutePath.Contains("/openai/v1", StringComparison.OrdinalIgnoreCase);

        // A throttled call is retried a bounded number of times and one attempt is bounded in time,
        // so the ceiling of a call is the timeout times the retries.
        if (isFoundryV1)
        {
            OpenAIClientOptions foundryOptions = new OpenAIClientOptions
            {
                Endpoint = endpoint,
                RetryPolicy = new ClientRetryPolicy(connection.MaxRetries),
                NetworkTimeout = TimeSpan.FromSeconds(connection.TimeoutSeconds)
            };
            return new OpenAIClient(new ApiKeyCredential(connection.ApiKey!), foundryOptions)
                .GetChatClient(options.ModelId)
                .AsIChatClient();
        }

        AzureOpenAIClientOptions azureOptions = new AzureOpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(connection.MaxRetries),
            NetworkTimeout = TimeSpan.FromSeconds(connection.TimeoutSeconds)
        };
        return new AzureOpenAIClient(endpoint, new AzureKeyCredential(connection.ApiKey!), azureOptions)
            .GetChatClient(options.ModelId)
            .AsIChatClient();
    }
}
