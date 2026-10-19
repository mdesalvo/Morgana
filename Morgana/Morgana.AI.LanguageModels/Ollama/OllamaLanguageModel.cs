using Microsoft.Extensions.AI;
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
/// verify your model is already loaded via "ollama ps".
/// </remarks>
public class OllamaLanguageModel : MorganaLanguageModel<OllamaTierConnection>
{
    /// <inheritdoc />
    // The name a tier writes in its Provider key: it must stay stable whatever the class is called.
    public override string Provider => "Ollama";

    /// <inheritdoc />
    public override bool IsContentFilterRefusal(Exception exception) =>
        // No refusal of this provider is known to reach the framework as an exception.
        false;

    /// <inheritdoc />
    protected override void ValidateConnection(OllamaTierConnection connection) =>
        RequireField(connection.Endpoint, nameof(OllamaTierConnection.Endpoint));

    /// <inheritdoc />
    protected override IChatClient CreateChatClient(OllamaTierConnection connection, Records.TierConfiguration options) =>
        // Ollama's client binds its model at construction, so each tier owns its HttpClient. The client has
        // no retry of its own: the connection carries no retry count.
        new OllamaApiClient(
            new HttpClient
            {
                BaseAddress = new Uri(connection.Endpoint!),
                Timeout = TimeSpan.FromSeconds(connection.TimeoutSeconds)
            },
            options.ModelId);
}

/// <summary>How a tier reaches Ollama.</summary>
/// <param name="Endpoint">Address of the Ollama service.</param>
/// <param name="TimeoutSeconds">Ceiling of one attempt.</param>
public record OllamaTierConnection(
    string? Endpoint = null,
    int TimeoutSeconds = 60);
