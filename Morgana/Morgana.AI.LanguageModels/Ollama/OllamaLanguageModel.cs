using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using OllamaSharp;

namespace Morgana.AI.LanguageModels.Ollama;

/// <summary>
/// Ollama provider.<br/>
/// Supports local models via Ollama interface (gpt-oss:20b, phi4-mini ...).
/// </summary>
/// <remarks>
/// Requires Endpoint on the tier's connection (e.g., http://localhost:11434). Choose a model with solid function calling
/// support (e.g., gpt-oss:20b, phi4-mini) because Morgana relies heavily on tool calling. Before starting Morgana,
/// verify your model is already loaded via "ollama ps". The plugin ships an empty pricing.json: a tier running an Ollama
/// model declares its own Pricing.
/// </remarks>
/// <param name="loggerFactory">Optional logger factory for the <see cref="AttemptLoggingHandler"/> that writes one line per attempt on the wire; <c>null</c> writes nothing.</param>
public class OllamaLanguageModel(ILoggerFactory? loggerFactory = null) : MorganaLanguageModel<OllamaTierConnection>
{
    /// <inheritdoc />
    // The name a tier writes in its Provider key: it must stay stable whatever the class is called.
    public override string Provider => "Ollama";

    /// <inheritdoc />
    protected override void ValidateConnection(OllamaTierConnection connection) =>
        RequireField(connection.Endpoint, nameof(OllamaTierConnection.Endpoint));

    /// <inheritdoc />
    protected override IChatClient CreateChatClient(OllamaTierConnection connection, Records.TierConfiguration options)
    {
        // The HTTP client carries the call's timeout and the attempt log. The client has
        // no retry of its own: the connection carries no retry count.
        HttpClient httpClient = AttemptLoggingHandler.CreateHttpClient(
            loggerFactory?.CreateLogger<OllamaLanguageModel>(), Provider, TimeSpan.FromSeconds(connection.TimeoutSeconds));

        // The endpoint is required by ValidateConnection, so it is present when a client is built.
        httpClient.BaseAddress = new Uri(connection.Endpoint!);

        // Ollama's client binds its model at construction, so each tier owns its HttpClient.
        return new OllamaApiClient(httpClient, options.ModelId);
    }
}

/// <summary>How a tier reaches Ollama.</summary>
/// <param name="Endpoint">Address of the Ollama service.</param>
/// <param name="TimeoutSeconds">Ceiling of one call: of its whole response when it is not streamed, of the wait for the response headers when it is.</param>
public record OllamaTierConnection(
    string? Endpoint = null,
    int TimeoutSeconds = 60);
