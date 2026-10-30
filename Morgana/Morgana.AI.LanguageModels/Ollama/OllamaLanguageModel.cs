using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using OllamaSharp;
using System.Net.Http.Headers;

namespace Morgana.AI.LanguageModels.Ollama;

/// <summary>
/// Ollama provider.<br/>
/// Supports local models and the cloud models that ollama.com hosts via Ollama interface (gpt-oss:20b, gemma4 ...).
/// </summary>
/// <remarks>
/// Requires Endpoint on the tier's connection. A local daemon (e.g., http://localhost:11434) bills the cloud models to
/// the account it signed in with; https://ollama.com bills the account of the ApiKey that it also requires. Choose a model
/// with solid function calling support (e.g., gpt-oss:20b, phi4-mini) because Morgana relies heavily on tool calling.
/// Before starting Morgana, verify that a local model is already loaded via "ollama ps". The embedded pricing.json
/// carries ollama.com's peak prices for its cloud models under the names that ollama.com answers to, cache write at the
/// input price since ollama.com bills none. A model that the pricing page lists without a cached input price reads its
/// cache at the input price. OllamaSharp reports no cached prompt tokens, so a cached prompt is charged at the input
/// price. A cloud model written for a daemon (gemma4:cloud, gpt-oss:120b-cloud) is priced under its catalogue name on
/// either endpoint. On a daemon a name without the cloud suffix is a local model, never priced from the catalogue: its
/// tier declares its own Pricing.
/// </remarks>
/// <param name="loggerFactory">Optional logger factory for the <see cref="AttemptLoggingHandler"/> that writes one line per attempt on the wire; <c>null</c> writes nothing.</param>
public class OllamaLanguageModel(ILoggerFactory? loggerFactory = null) : MorganaLanguageModel<OllamaTierConnection>
{
    /// <inheritdoc />
    // The name a tier writes in its Provider key: it must stay stable whatever the class is called.
    public override string Provider => "Ollama";

    // Host of Ollama's own service: the one endpoint that bills an account and so demands its key.
    private const string CloudHost = "ollama.com";

    // The tag that names a cloud model with no size of its own, as in gemma4:cloud.
    private const string CloudTag = ":cloud";

    // The suffix that turns a size tag into a cloud model, as in gpt-oss:120b-cloud.
    private const string CloudTagSuffix = "-cloud";

    /// <summary>Tells whether an endpoint is Ollama's own cloud service.</summary>
    private static bool IsOllamaCloud(string endpoint)
    {
        // An endpoint that is not an absolute address is no cloud host: it fails later where the client is built.
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? endpointUri))
            return false;

        // Only the cloud host itself bills an account.
        return string.Equals(endpointUri.Host, CloudHost, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    protected override void ValidateConnection(OllamaTierConnection connection)
    {
        // Every tier reaches Ollama through an endpoint.
        RequireField(connection.Endpoint, nameof(OllamaTierConnection.Endpoint));

        // A local daemon carries its own sign-in; ollama.com bills the account whose key the call carries.
        if (IsOllamaCloud(connection.Endpoint!))
            RequireField(connection.ApiKey, nameof(OllamaTierConnection.ApiKey));
    }

    /// <inheritdoc />
    protected override IChatClient CreateChatClient(OllamaTierConnection connection, Records.TierConfiguration options)
    {
        // The HTTP client carries the call's timeout and the attempt log. The client has
        // no retry of its own: the connection carries no retry count.
        HttpClient httpClient = AttemptLoggingHandler.CreateHttpClient(
            loggerFactory?.CreateLogger<OllamaLanguageModel>(), Provider, TimeSpan.FromSeconds(connection.TimeoutSeconds));

        // The endpoint is required by ValidateConnection, so it is present when a client is built.
        httpClient.BaseAddress = new Uri(connection.Endpoint!);

        // ollama.com bills the account whose key the call carries; ValidateConnection already guaranteed the key.
        if (IsOllamaCloud(connection.Endpoint!))
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);

        // Ollama's client binds its model at construction, so each tier owns its HttpClient.
        return new OllamaApiClient(httpClient, options.ModelId);
    }

    /// <inheritdoc />
    protected override string? CataloguedModelId(OllamaTierConnection connection, string modelId)
    {
        // A cloud tag without a size is priced under the catalogue name that it follows, on the daemon and on ollama.com alike.
        if (modelId.EndsWith(CloudTag, StringComparison.Ordinal))
            return modelId[..^CloudTag.Length];

        // A size tag with the cloud suffix is priced under the catalogue name once the suffix is cut away.
        if (modelId.Contains(':', StringComparison.Ordinal) && modelId.EndsWith(CloudTagSuffix, StringComparison.Ordinal))
            return modelId[..^CloudTagSuffix.Length];

        // On ollama.com every model is a cloud model under its own name. On a daemon a name without the cloud suffix
        // is a local model, which the catalogue never prices. The endpoint is present because ValidateConnection ran first.
        return IsOllamaCloud(connection.Endpoint!) ? modelId : null;
    }
}

/// <summary>How a tier reaches Ollama.</summary>
/// <param name="Endpoint">Address of the Ollama service.</param>
/// <param name="ApiKey">Credential of the ollama.com account that pays for the cloud models; required and sent only when <paramref name="Endpoint"/> is ollama.com.</param>
/// <param name="TimeoutSeconds">Ceiling of one call: of its whole response when it is not streamed, of the wait for the response headers when it is.</param>
public record OllamaTierConnection(
    string? Endpoint = null,
    string? ApiKey = null,
    int TimeoutSeconds = 60);
