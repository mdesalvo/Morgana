using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;

namespace Morgana.AI.LanguageModels.Anthropic;

/// <summary>
/// The Anthropic provider, serving Claude models such as claude-haiku-5-5, claude-sonnet-5-5 and claude-opus-5-5 on a tier's ApiKey.
/// Its models.json carries the standard prices with the 1-hour cache write, since <see cref="MorganaAnthropicClient"/> asks for a cache
/// that lives an hour; Claude Haiku 5.5 is priced at its band for prompts up to 100,000 tokens.
/// </summary>
/// <param name="loggerFactory">
/// Writes the attempt lines of <see cref="AttemptLoggingHandler"/> and the diagnostics of <see cref="MorganaAnthropicClient"/>;
/// <c>null</c> silences both while the message list is still normalized.
/// </param>
public class AnthropicLanguageModel(ILoggerFactory? loggerFactory = null) : MorganaLanguageModel<AnthropicTierConnection>
{
    /// <summary>The name a tier writes in its Provider key, which stays the same whatever the class is called.</summary>
    public override string Provider => "Anthropic";

    /// <inheritdoc />
    protected override void ValidateConnection(AnthropicTierConnection connection) =>
        RequireField(connection.ApiKey, nameof(AnthropicTierConnection.ApiKey));

    /// <summary>Refuses a tier whose model declares no MaxOutputTokens, neither in the tier nor in models.json.</summary>
    public override void ValidateOptions(Records.ModelOptions options)
    {
        // Without MaxOutputTokens the SDK adapter sends 1024 tokens and the reasoning tokens count against them, so a thinking model is cut short.
        if (options.MaxOutputTokens is null)
            throw new InvalidOperationException("MaxOutputTokens is missing: Anthropic needs an output ceiling on every call and neither the tier nor the provider's models.json declares one for this model.");
    }

    /// <summary>
    /// Builds the tier's client: the SDK adapter inside <see cref="MorganaAnthropicClient"/>.
    /// The decorator ends every request on a role that Claude accepts and marks the leading system prompt for caching.
    /// </summary>
    protected override IChatClient CreateChatClient(AnthropicTierConnection connection, Records.ModelOptions options)
    {
        // Left to its defaults the SDK retries a rate-limited or overloaded call on its own, with a
        // backoff nobody sees. MaxRetries and the per-attempt timeout bound a throttled call and
        // the handler writes down every attempt. The timeout bounds one attempt's wait for the response
        // headers only: a body that stalls after them, streamed or not, is bounded by no SDK setting
        // and only the caller's own silence budget ends it.
        AnthropicClient anthropicClient = new AnthropicClient(
            new ClientOptions
            {
                ApiKey = connection.ApiKey!,
                MaxRetries = connection.MaxRetries,
                Timeout = TimeSpan.FromSeconds(connection.TimeoutSeconds),
                Handlers = [new AttemptLoggingHandler(loggerFactory?.CreateLogger<AnthropicLanguageModel>(), Provider)]
            });

        // The decorator is what the framework talks to: it keeps requests inside Claude's API rules and marks the system prompt for caching.
        return new MorganaAnthropicClient(anthropicClient.AsIChatClient(options.ModelId), loggerFactory);
    }
}

/// <summary>How a tier reaches Anthropic, bound from the tier's Connection with the shared Reliability laid under it.</summary>
/// <param name="ApiKey">Credential of the Anthropic account.</param>
/// <param name="MaxRetries">Retries of a refused call.</param>
/// <param name="TimeoutSeconds">How long one attempt waits for the response headers; the body that follows is not bounded by it.</param>
public record AnthropicTierConnection(
    string? ApiKey = null,
    int MaxRetries = 2,
    int TimeoutSeconds = 60);