using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;

namespace Morgana.AI.LanguageModels.Google;

/// <summary>
/// Google provider.<br/>
/// Supports Gemini models through the Gemini API (gemini-3.5-flash-lite, gemini-3.8-flash, gemini-4-argon, ...)
/// </summary>
/// <remarks>
/// Requires ApiKey on the tier's connection. The embedded pricing.json carries Google's standard processing prices for
/// prompts up to 200,000 tokens, the ones in force once each promotion ends: Gemini bills a cache write as plain input,
/// so the write price equals the input price. Gemini reports a blocked prompt or answer inside the response, so none of
/// its refusals reaches the framework as an exception.
/// </remarks>
/// <param name="loggerFactory">Optional logger factory for the <see cref="AttemptLoggingHandler"/> that writes one line per attempt on the wire; <c>null</c> writes nothing.</param>
public class GoogleLanguageModel(ILoggerFactory? loggerFactory = null) : MorganaLanguageModel<GoogleTierConnection>
{
    /// <inheritdoc />
    // The name a tier writes in its Provider key: it must stay stable whatever the class is called.
    public override string Provider => "Google";

    /// <inheritdoc />
    protected override void ValidateConnection(GoogleTierConnection connection) =>
        RequireField(connection.ApiKey, nameof(GoogleTierConnection.ApiKey));

    /// <inheritdoc />
    protected override IChatClient CreateChatClient(GoogleTierConnection connection, Records.TierConfiguration options)
    {
        // The SDK retries a throttled or failed attempt on its own and its count includes the first attempt.
        // Its Timeout stays unset: for a streamed answer it would bound the whole body, which only the caller's silence budget may end.
        HttpOptions httpOptions = new HttpOptions
        {
            RetryOptions = new HttpRetryOptions { Attempts = connection.MaxRetries + 1 }
        };

        // The HTTP client bounds one attempt's wait for the response headers and its handler writes down every attempt.
        ClientOptions clientOptions = new ClientOptions
        {
            HttpClientFactory = () => AttemptLoggingHandler.CreateHttpClient(
                loggerFactory?.CreateLogger<GoogleLanguageModel>(), Provider, TimeSpan.FromSeconds(connection.TimeoutSeconds))
        };

        // The tier's connection alone decides how Gemini is reached, so no environment variable can switch it to Vertex AI or to another key.
        // The key is required by ValidateConnection, so it is present when a client is built.
        Client googleClient = new Client(enterprise: false, vertexAI: false, apiKey: connection.ApiKey!, httpOptions: httpOptions, clientOptions: clientOptions);

        // The decorator is what the framework talks to: it reports the model's thinking as output, which Gemini bills it as.
        return new MorganaGoogleClient(googleClient.AsIChatClient(options.ModelId));
    }
}

/// <summary>How a tier reaches Google's Gemini API.</summary>
/// <param name="ApiKey">Credential of the Gemini API project.</param>
/// <param name="MaxRetries">Retries of a refused call.</param>
/// <param name="TimeoutSeconds">Ceiling of one attempt's wait for the response headers; the body that follows is not bounded by it.</param>
public record GoogleTierConnection(
    string? ApiKey = null,
    int MaxRetries = 2,
    int TimeoutSeconds = 60);