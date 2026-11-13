using System.ClientModel;
using System.ClientModel.Primitives;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using OpenAI;

namespace Morgana.AI.LanguageModels.AzureOpenAI;

/// <summary>
/// The Azure OpenAI provider, serving deployments of a classic Azure OpenAI resource or of an Azure AI Foundry project on a tier's ApiKey and Endpoint.
/// A classic endpoint looks like https://resource.openai.azure.com, a Foundry one like https://resource.services.ai.azure.com/api/projects/X/openai/v1.
/// A tier's ModelId names a deployment the customer chose, so models.json is empty and a tier on Azure declares its own Pricing.
/// </summary>
/// <param name="loggerFactory">Writes the attempt lines of <see cref="AttemptLoggingHandler"/>; <c>null</c> writes nothing.</param>
public class AzureOpenAILanguageModel(ILoggerFactory? loggerFactory = null) : MorganaLanguageModel<AzureOpenAITierConnection>
{
    /// <summary>The name a tier writes in its Provider key, which stays the same whatever the class is called.</summary>
    public override string Provider => "AzureOpenAI";

    /// <inheritdoc />
    public override bool IsContentFilterRefusal(Exception exception) =>
        // Azure's own content filter (Prompt Shields included) blocks the prompt before any answer and reports it as a 400 naming content_filter.
        exception is ClientResultException { Status: 400 } clientResultException && clientResultException.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    protected override void ValidateConnection(AzureOpenAITierConnection connection)
    {
        // Both endpoint styles are reached with a key at an address, so a tier lacking either is refused at startup.
        RequireField(connection.ApiKey, nameof(AzureOpenAITierConnection.ApiKey));
        RequireField(connection.Endpoint, nameof(AzureOpenAITierConnection.Endpoint));
    }

    /// <inheritdoc />
    protected override IChatClient CreateChatClient(AzureOpenAITierConnection connection, Records.ModelOptions options)
    {
        // The endpoint is required by ValidateConnection, so it is present when a client is built.
        Uri endpoint = new Uri(connection.Endpoint!);

        // A Foundry project's v1 API, whose path contains "/openai/v1", rejects the api-version query parameter
        // that the Azure client always appends, so such an endpoint is reached with the plain OpenAI client.
        bool isFoundryV1 = endpoint.AbsolutePath.Contains("/openai/v1", StringComparison.OrdinalIgnoreCase);

        // The pipeline's NetworkTimeout bounds each attempt, so the HTTP client that writes the attempt lines
        // sets none of its own: its default 100 seconds would cut first. Both endpoint styles share it.
        HttpClientPipelineTransport transport = new HttpClientPipelineTransport(
            AttemptLoggingHandler.CreateHttpClient(loggerFactory?.CreateLogger<AzureOpenAILanguageModel>(), Provider, Timeout.InfiniteTimeSpan));

        // Both endpoint styles end in an OpenAIClient (the Azure client is one) so that a single chat client is derived below.
        OpenAIClient openAIClient;
        if (isFoundryV1)
        {
            // A throttled call is retried a bounded number of times. NetworkTimeout bounds the wait for the headers
            // and each wait between two reads of the body; a streamed body that it cuts is not retried.
            OpenAIClientOptions foundryOptions = new OpenAIClientOptions
            {
                Endpoint = endpoint,
                RetryPolicy = new ClientRetryPolicy(connection.MaxRetries),
                NetworkTimeout = TimeSpan.FromSeconds(connection.TimeoutSeconds),
                Transport = transport
            };

            // The Foundry client is bounded like the classic one but speaks the plain OpenAI protocol.
            openAIClient = new OpenAIClient(new ApiKeyCredential(connection.ApiKey!), foundryOptions);
        }
        else
        {
            // The classic resource takes the same retry policy and timeout as a Foundry project.
            AzureOpenAIClientOptions azureOptions = new AzureOpenAIClientOptions
            {
                RetryPolicy = new ClientRetryPolicy(connection.MaxRetries),
                NetworkTimeout = TimeSpan.FromSeconds(connection.TimeoutSeconds),
                Transport = transport
            };

            // A classic resource is served by the Azure client, which adds the api-version it requires.
            openAIClient = new AzureOpenAIClient(endpoint, new AzureKeyCredential(connection.ApiKey!), azureOptions);
        }

        // The tier's ModelId names the deployment that serves it, whichever endpoint style the resource exposes.
        return openAIClient.GetChatClient(options.ModelId).AsIChatClient();
    }
}

/// <summary>How a tier reaches Azure OpenAI, bound from the tier's Connection with the shared Reliability laid under it.</summary>
/// <param name="ApiKey">Credential of the Azure account.</param>
/// <param name="Endpoint">Address of the Azure OpenAI resource or of the Azure AI Foundry project.</param>
/// <param name="MaxRetries">Retries of a refused call.</param>
/// <param name="TimeoutSeconds">How long one attempt waits for the response headers and between two reads of its body; a streamed body cut by it is not retried.</param>
public record AzureOpenAITierConnection(
    string? ApiKey = null,
    string? Endpoint = null,
    int MaxRetries = 2,
    int TimeoutSeconds = 60);