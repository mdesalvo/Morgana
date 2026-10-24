using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Morgana.AI.Abstractions;

/// <summary>
/// One LLM provider: builds the chat client of a single tier from that tier's connection section and options.
/// </summary>
public abstract class MorganaLanguageModel
{
    /// <summary>The name that a tier writes in <c>Morgana:LanguageModels:Tiers:{tier}:Provider</c> to be served by this provider.</summary>
    public abstract string Provider { get; }

    /// <summary>
    /// Refuses a connection that lacks a field that this provider uses.
    /// </summary>
    /// <remarks>
    /// The message opens with the name of the field so that the caller can put the key path of the tier in front of it.
    /// </remarks>
    /// <param name="connectionSection">The <c>Connection</c> section of the tier, which exists when the provider is asked.</param>
    /// <exception cref="InvalidOperationException">A field that the provider uses is missing, empty or still a placeholder.</exception>
    public abstract void ValidateConnection(IConfigurationSection connectionSection);

    /// <summary>Builds the provider client that serves one tier, with no telemetry and no tier defaults around it.</summary>
    /// <param name="connectionSection">The <c>Connection</c> section of the tier: how it reaches the provider.</param>
    /// <param name="options">The model that the tier runs.</param>
    public abstract IChatClient CreateChatClient(IConfigurationSection connectionSection, Records.TierConfiguration options);

    /// <summary>Tells whether an exception thrown by this provider's client is the provider refusing the content itself, a refusal that no retry would pass.</summary>
    /// <param name="exception">The failure that the client of a tier threw.</param>
    public abstract bool IsContentFilterRefusal(Exception exception);

    /// <summary>What a model costs in US dollars; <c>null</c> when this provider does not know the model. A provider author declares here the prices of the models it serves.</summary>
    /// <param name="modelId">The model that a tier names in its <c>Options:ModelId</c>.</param>
    protected abstract Records.ModelPricing? GetModelPricing(string modelId);

    /// <summary>One dust unit is worth this many US dollars: the one place where that value exists.</summary>
    private const decimal UsdPerDustUnit = 0.015m;

    /// <summary>Tokens in the million that a price is quoted per.</summary>
    private const decimal TokensPerPricedMillion = 1_000_000m;

    /// <summary>Converts the price of a model into Morgana's dust, the one conversion for every provider; <c>null</c> when <see cref="GetModelPricing"/> does not know the model.</summary>
    /// <param name="modelId">The model that a tier names in its <c>Options:ModelId</c>.</param>
    public Records.MagicDustPricing? ConvertToMorganaDust(string modelId)
    {
        // The provider alone knows what its models cost.
        Records.ModelPricing? modelPricing = GetModelPricing(modelId);

        // A model that the provider does not price has no dust.
        if (modelPricing is null)
            return null;

        // Tokens that one dust unit buys in each direction; a free direction keeps 0, which means "not charged".
        // Cache weights are relative to a fresh input token and stay 0 while the input is free.
        return new Records.MagicDustPricing
        {
            InputTokensPerDustUnit = TokensPerDustUnit(modelPricing.InputUsdPerMillion),
            OutputTokensPerDustUnit = TokensPerDustUnit(modelPricing.OutputUsdPerMillion),
            CachedInputWeight = modelPricing.InputUsdPerMillion == 0m ? 0d : (double)(modelPricing.CacheReadUsdPerMillion / modelPricing.InputUsdPerMillion),
            CacheCreationWeight = modelPricing.InputUsdPerMillion == 0m ? 0d : (double)(modelPricing.CacheWriteUsdPerMillion / modelPricing.InputUsdPerMillion)
        };
    }

    /// <summary>Tokens that one dust unit buys at a price per million tokens; 0 for a free direction.</summary>
    private static int TokensPerDustUnit(decimal usdPerMillion) =>
        // A free direction must not divide by zero: 0 is the meaning of "not charged".
        usdPerMillion == 0m
            ? 0
            : (int)Math.Round(TokensPerPricedMillion * UsdPerDustUnit / usdPerMillion, MidpointRounding.AwayFromZero);

    /// <summary>Refuses a value that is missing, empty or still a placeholder, naming <paramref name="field"/> first.</summary>
    protected static void RequireField(string? value, string field)
    {
        // A blank value or a shipped placeholder means the deployment never supplied the secret: the host must not start on it.
        if (string.IsNullOrWhiteSpace(value) || Constants.SecretOverrides.All.Contains(value))
            throw new InvalidOperationException($"{field} is missing or still a placeholder. Override it via User Secrets or environment variables.");
    }
}

/// <summary>
/// A provider that binds the <c>Connection</c> section of its tier to <typeparamref name="TConnection"/>.
/// </summary>
/// <remarks>
/// A provider author declares a connection record holding exactly the fields that the provider honours and never touches <see cref="IConfiguration"/>.
/// </remarks>
/// <typeparam name="TConnection">The connection shape that only this provider knows.</typeparam>
public abstract class MorganaLanguageModel<TConnection> : MorganaLanguageModel
{
    /// <inheritdoc />
    public sealed override void ValidateConnection(IConfigurationSection connectionSection) =>
        // The provider sees its own connection shape and never the raw section.
        ValidateConnection(Bind(connectionSection));

    /// <inheritdoc />
    public sealed override IChatClient CreateChatClient(IConfigurationSection connectionSection, Records.TierConfiguration options) =>
        // The client is built from the same typed connection that validation saw.
        CreateChatClient(Bind(connectionSection), options);

    /// <summary>Refuses a connection that lacks a field that this provider uses, naming the field first.</summary>
    /// <param name="connection">The connection of the tier as this provider reads it.</param>
    /// <exception cref="InvalidOperationException">A field that the provider uses is missing, empty or still a placeholder.</exception>
    protected abstract void ValidateConnection(TConnection connection);

    /// <summary>Builds the provider client that serves one tier from its typed connection.</summary>
    /// <param name="connection">How the tier reaches the provider.</param>
    /// <param name="options">The model that the tier runs.</param>
    protected abstract IChatClient CreateChatClient(TConnection connection, Records.TierConfiguration options);

    /// <summary>Reads the connection section into the shape that this provider declares.</summary>
    private static TConnection Bind(IConfigurationSection connectionSection) =>
        // ConfigurationLanguageModelService refuses a tier without a Connection section before any provider is asked, so the binder finds one.
        connectionSection.Get<TConnection>()!;
}

/// <summary>Writes down every attempt that a provider's SDK puts on the wire.</summary>
/// <remarks>It sits under the SDK's own retry loop so that a throttled or cut attempt is told apart from a slow model.
/// It observes an attempt up to its response headers and never alters it: the SDK alone decides whether to try again.</remarks>
/// <param name="logger">Receives one line per attempt; <c>null</c> writes nothing.</param>
/// <param name="providerName">The provider's name as the lines show it.</param>
public sealed class AttemptLoggingHandler(ILogger? logger, string providerName) : DelegatingHandler
{
    /// <summary>The HTTP client that a provider hands its SDK when the SDK takes a client rather than handlers; <paramref name="timeout"/> is <see cref="Timeout.InfiniteTimeSpan"/> when the SDK bounds each attempt itself.</summary>
    public static HttpClient CreateHttpClient(ILogger? logger, string providerName, TimeSpan timeout) =>
        // The handler wraps the socket layer so that it sees each attempt that the SDK sends.
        new(new AttemptLoggingHandler(logger, providerName) { InnerHandler = new SocketsHttpHandler() }) { Timeout = timeout };

    /// <summary>Sends the attempt and writes the line that tells how it went.</summary>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // The attempt's clock starts before the wire is touched so that every elapsed time includes the wait for the headers.
        long startedAt = Stopwatch.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            // The attempt goes out untouched.
            response = await base.SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException)
        {
            logger?.LogWarning("{Provider} gave no answer after {Elapsed:0.0}s ({Reason}); the client retries it if it has attempts left",
                providerName, Stopwatch.GetElapsedTime(startedAt).TotalSeconds, exception.GetType().Name);
            // The SDK must see the failure as it was: only it decides whether to try again.
            throw;
        }

        // The wait for the headers is what every line reports: the body that follows is read by the SDK, past this handler.
        double elapsed = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;

        // A throttled or overloaded answer is a warning because the SDK is about to wait and try again; any other answer is routine.
        if (response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError)
        {
            // The wait that the provider asks for is what the SDK honours before its next attempt.
            string retryAfter = response.Headers.RetryAfter?.Delta is TimeSpan delta
                ? $"{delta.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)}s"
                : "an unstated time";
            logger?.LogWarning("{Provider} refused a call with {Status} after {Elapsed:0.0}s and asks to wait {RetryAfter}; the client retries it if it has attempts left",
                providerName, (int)response.StatusCode, elapsed, retryAfter);
        }
        else
            logger?.LogInformation("{Provider} answered {Status} after {Elapsed:0.0}s", providerName, (int)response.StatusCode, elapsed);

        // The response travels on untouched: the handler only observes the attempt.
        return response;
    }
}