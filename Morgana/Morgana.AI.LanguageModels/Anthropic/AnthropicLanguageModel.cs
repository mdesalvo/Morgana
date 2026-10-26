using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;

namespace Morgana.AI.LanguageModels.Anthropic;

/// <summary>
/// Anthropic provider.<br/>
/// Supports Claude models (claude-haiku-5-5, claude-sonnet-5-5, claude-opus-5-5, ...)
/// </summary>
/// <remarks>
/// Requires ApiKey on the tier's connection. The embedded pricing.json carries Anthropic's standard processing prices:
/// its cache write is the 1-hour write because <see cref="MorganaAnthropicClient"/> asks for a cache that lives an hour,
/// while Claude Haiku 5.5 is priced at its band for prompts up to 100,000 tokens.
/// </remarks>
/// <param name="loggerFactory">
/// Optional logger factory used by <see cref="MorganaAnthropicClient"/> and by the <see cref="AttemptLoggingHandler"/> that writes one line per attempt on the wire. When <c>null</c>, the guard's
/// diagnostic channel is silent but the message-list normalization still applies.
/// </param>
public class AnthropicLanguageModel(ILoggerFactory? loggerFactory = null) : MorganaLanguageModel<AnthropicTierConnection>
{
    /// <inheritdoc />
    // The name a tier writes in its Provider key: it must stay stable whatever the class is called.
    public override string Provider => "Anthropic";

    /// <inheritdoc />
    protected override void ValidateConnection(AnthropicTierConnection connection) =>
        RequireField(connection.ApiKey, nameof(AnthropicTierConnection.ApiKey));

    /// <inheritdoc />
    /// <remarks>
    /// The SDK adapter is wrapped in <see cref="MorganaAnthropicClient"/>, which ends every request on a role
    /// that Claude accepts and marks the leading system message for prompt caching.
    /// </remarks>
    protected override IChatClient CreateChatClient(AnthropicTierConnection connection, Records.TierConfiguration options)
    {
        // Left to its defaults the SDK retries a rate-limited or overloaded call on its own, with a
        // backoff nobody sees. The retry ceiling and the per-attempt timeout bound a throttled call and
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

/// <summary>How a tier reaches Anthropic.</summary>
/// <param name="ApiKey">Credential of the Anthropic account.</param>
/// <param name="MaxRetries">Retries of a refused call.</param>
/// <param name="TimeoutSeconds">Ceiling of one attempt's wait for the response headers; the body that follows is not bounded by it.</param>
public record AnthropicTierConnection(
    string? ApiKey = null,
    int MaxRetries = 2,
    int TimeoutSeconds = 60);
