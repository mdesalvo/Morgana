using Microsoft.Extensions.AI;
using Morgana.AI.Abstractions;
using OllamaSharp;

namespace Morgana.AI.LanguageModels;

/// <summary>
/// Ollama provider.<br/>
/// Supports local models via Ollama interface (gpt-oss:20b, phi4-mini ...).
/// </summary>
/// <remarks>
/// Requires Endpoint on the tier's connection (e.g., http://localhost:11434). Choose a model with solid function calling
/// support (e.g., gpt-oss:20b, phi4-mini) because Morgana relies heavily on tool calling. Before starting Morgana,
/// verify your model is already loaded via "ollama ps".
/// </remarks>
public class Ollama : MorganaLanguageModel
{
    /// <inheritdoc />
    /// <remarks>Ollama's API has no way to name the tool a model must call and its client drops the request.</remarks>
    public override bool CanForceToolCall => false;

    /// <inheritdoc />
    public override void ValidateConnection(Records.TierConnection connection) =>
        RequireField(connection.Endpoint, nameof(Records.TierConnection.Endpoint));

    /// <inheritdoc />
    public override IChatClient CreateChatClient(Records.TierConnection connection, Records.TierConfiguration options) =>
        // Ollama's client binds its model at construction, so each tier owns its HttpClient. The client has
        // no retry of its own: MaxRetries does not apply to this provider.
        new OllamaApiClient(
            new HttpClient
            {
                BaseAddress = new Uri(connection.Endpoint!),
                Timeout = TimeSpan.FromSeconds(connection.TimeoutSeconds)
            },
            options.ModelId);
}
