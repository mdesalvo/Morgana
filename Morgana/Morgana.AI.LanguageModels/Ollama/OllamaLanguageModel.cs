using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using OllamaSharp;
using OllamaSharp.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Morgana.AI.LanguageModels.Ollama;

/// <summary>
/// The Ollama provider, serving local models on a daemon such as http://localhost:11434 and the cloud models of ollama.com, whose key a call there carries.
/// Its models.json carries ollama.com's peak prices under the names ollama.com answers to, cache write and any unlisted cache read at the input price;
/// OllamaSharp reports no cached prompt tokens. A daemon's cloud name (gemma4:cloud, gpt-oss:120b-cloud) is priced under its catalogue name, while a
/// daemon's name without the cloud suffix is a local model whose tier declares its own Pricing.
/// AdditionalProperties:num_ctx, 65536 when absent, holds prompt and answer together, so it must exceed MaxOutputTokens by the longest prompt the tier's agents read.
/// </summary>
/// <param name="loggerFactory">Writes the attempt lines of <see cref="AttemptLoggingHandler"/>; <c>null</c> writes nothing.</param>
public class OllamaLanguageModel(ILoggerFactory? loggerFactory = null) : MorganaLanguageModel<OllamaTierConnection>
{
    /// <summary>The name a tier writes in its Provider key, which stays the same whatever the class is called.</summary>
    public override string Provider => "Ollama";

    /// <summary>The num_ctx a tier asks for when it declares none, since Ollama's own default is small and silently cuts the start of a long prompt.</summary>
    private const int DefaultContextLength = 65536;

    /// <summary>The type of every option OllamaSharp sends inside a request's options, read from the library's own model so it follows the library.</summary>
    private static readonly Dictionary<string, Type> RequestOptionTypes =
        typeof(RequestOptions).GetProperties()
        .Where(property => property.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
        .ToDictionary(
            property => property.GetCustomAttribute<JsonPropertyNameAttribute>()!.Name,
            property => Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType,
            StringComparer.Ordinal);

    /// <summary>The host of Ollama's own service: the one endpoint that bills an account and so demands its key.</summary>
    private const string CloudHost = "ollama.com";

    /// <summary>The tag that names a cloud model with no size of its own, as in gemma4:cloud.</summary>
    private const string CloudTag = ":cloud";

    /// <summary>The suffix that turns a size tag into a cloud model, as in gpt-oss:120b-cloud.</summary>
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

    /// <summary>Turns an option written in configuration into the type that OllamaSharp sends it as.</summary>
    private static object? ConvertOption(string optionName, object? optionValue)
    {
        // Only text from configuration needs converting and only an option that OllamaSharp types; anything else reaches Ollama as written.
        if (optionValue is not string optionText || !RequestOptionTypes.TryGetValue(optionName, out Type? optionType))
            return optionValue;

        // A boolean option such as use_mmap is read as true or false.
        if (optionType == typeof(bool))
            return bool.Parse(optionText);

        // An integer option such as num_ctx is read whatever the host's culture.
        if (optionType == typeof(int))
            return int.Parse(optionText, NumberStyles.Integer, CultureInfo.InvariantCulture);

        // A decimal option such as min_p is read with a dot whatever the host's culture.
        if (optionType == typeof(float))
            return float.Parse(optionText, NumberStyles.Float, CultureInfo.InvariantCulture);

        // A list option such as stop is written through ChatOptions.StopSequences and is left as it came.
        return optionValue;
    }

    /// <inheritdoc />
    public override void ValidateOptions(Records.ModelOptions options)
    {
        // Every option that the tier declares is tried in the type that Ollama will receive it as; a tier that declares none has nothing to convert.
        if (options.AdditionalProperties is not null)
        {
            foreach (KeyValuePair<string, object?> additionalProperty in options.AdditionalProperties)
            {
                try
                {
                    // The conversion every call will make is tried once at startup, so a value Ollama cannot read stops Morgana instead of the first call.
                    ConvertOption(additionalProperty.Key, additionalProperty.Value);
                }
                catch (Exception ex) when (ex is FormatException or OverflowException)
                {
                    // The lookup cannot fail because only a typed option can fail to convert.
                    Type optionType = RequestOptionTypes[additionalProperty.Key];

                    // The form named is the one the deployer must write.
                    string expectedForm = optionType == typeof(bool) ? "true or false" : optionType == typeof(int) ? "an integer" : "a number written with a dot";

                    // The message opens with the option's key so that the caller prefixes the tier's path.
                    throw new InvalidOperationException($"AdditionalProperties:{additionalProperty.Key} must be {expectedForm} but is '{additionalProperty.Value}'.", ex);
                }
            }
        }

        // The num_ctx that the tier declares, already proven above to read as an integer; when it declares none, every call asks for Morgana's default.
        object? declaredNumCtx = null;
        bool isNumCtxDeclared = options.AdditionalProperties?.TryGetValue(OllamaOption.NumCtx.Name, out declaredNumCtx) == true;
        int numCtx = isNumCtxDeclared && ConvertOption(OllamaOption.NumCtx.Name, declaredNumCtx) is int declaredNumCtxValue
            ? declaredNumCtxValue
            : DefaultContextLength;

        // A MaxOutputTokens that reaches num_ctx promises answers that no prompt leaves room for: the tier stops the startup instead of the call.
        if (options.MaxOutputTokens is { } maxOutputTokens && numCtx <= maxOutputTokens)
        {
            // The deployer reads where num_ctx came from, since a default is a value they never wrote.
            string numCtxOrigin = isNumCtxDeclared ? string.Empty : " (Morgana's default, since the tier declares none)";

            // The message opens with the option's key so that the caller prefixes the tier's path.
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"AdditionalProperties:{OllamaOption.NumCtx.Name} is {numCtx}{numCtxOrigin}, which should be greater than MaxOutputTokens {maxOutputTokens}: num_ctx must hold the prompt besides the answer."));
        }
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
    protected override IChatClient CreateChatClient(OllamaTierConnection connection, Records.ModelOptions options)
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
        // The call is wrapped so that a stopped call ends at once.
        IChatClient ollamaClient = new MorganaOllamaClient(new OllamaApiClient(httpClient, options.ModelId));

        // Every option of the tier reaches OllamaSharp in the type it reads.
        return new ChatClientBuilder(ollamaClient)
            .ConfigureOptions(chatOptions =>
            {
                // The tier may have filled in no option at all.
                chatOptions.AdditionalProperties ??= new AdditionalPropertiesDictionary();

                // Each option the tier filled in above is converted to the type that OllamaSharp reads.
                foreach (KeyValuePair<string, object?> additionalProperty in chatOptions.AdditionalProperties.ToList())
                    chatOptions.AdditionalProperties[additionalProperty.Key] = ConvertOption(additionalProperty.Key, additionalProperty.Value);

                // The window travels on every call so that the server never falls back to its own default and cuts the start of the prompt;
                // a window that a tier declared is left as it is.
                chatOptions.AdditionalProperties.TryAdd(OllamaOption.NumCtx.Name, DefaultContextLength);
            })
            .Build();
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

/// <summary>How a tier reaches Ollama, bound from the tier's Connection; it carries no MaxRetries since the client makes no retry of its own.</summary>
/// <param name="Endpoint">Address of the Ollama service.</param>
/// <param name="ApiKey">Credential of the ollama.com account that pays for the cloud models; required and sent only when <paramref name="Endpoint"/> is ollama.com.</param>
/// <param name="TimeoutSeconds">How long one call may take: its whole response when it is not streamed, the wait for the response headers when it is.</param>
public record OllamaTierConnection(
    string? Endpoint = null,
    string? ApiKey = null,
    int TimeoutSeconds = 60);