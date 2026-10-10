using System.Diagnostics;
using System.Globalization;
using System.Net;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.ChatClients;

namespace Morgana.AI.LanguageModels;

/// <summary>
/// Anthropic provider.<br/>
/// Supports Claude models (claude-fable-5, claude-sonnet-5, ...)
/// </summary>
/// <remarks>
/// Requires ApiKey on the tier's connection. MagicDust pricing is per tier: recalibrate InputTokensPerDustUnit
/// and OutputTokensPerDustUnit when a tier's ModelId changes.
/// </remarks>
/// <param name="loggerFactory">
/// Optional logger factory used by <see cref="MorganaAnthropicClient"/> and the attempt log. When <c>null</c>, the guard's
/// diagnostic channel is silent but the message-list normalization still applies.
/// </param>
public class Anthropic(ILoggerFactory? loggerFactory = null) : MorganaLanguageModel
{
    /// <inheritdoc />
    public override void ValidateConnection(Records.TierConnection connection) =>
        RequireField(connection.ApiKey, nameof(Records.TierConnection.ApiKey));

    /// <inheritdoc />
    /// <remarks>
    /// The SDK adapter is wrapped in <see cref="MorganaAnthropicClient"/>, which enforces the Claude 4.6+
    /// no-prefill constraint at the API boundary and marks the leading system message for prompt caching.
    /// </remarks>
    public override IChatClient CreateChatClient(Records.TierConnection connection, Records.TierConfiguration options)
    {
        // Left to its defaults the SDK retries a rate-limited or overloaded call on its own, with a
        // backoff nobody sees and no ceiling anybody chose: a turn that is being throttled is
        // indistinguishable from a turn that is thinking, for minutes, while the caller waits it out
        // with nothing on the screen and nothing in the log. The timeout bounds ONE attempt, so the
        // worst case of a call is it times the retries — which is what any ceiling above has to be
        // read against, since a caller's own budget covers a whole agentic loop of such calls.
        AnthropicClient anthropicClient = new AnthropicClient(
            new ClientOptions
            {
                ApiKey = connection.ApiKey!,
                MaxRetries = connection.MaxRetries,
                Timeout = TimeSpan.FromSeconds(connection.TimeoutSeconds),
                Handlers = [new AttemptLogger(loggerFactory?.CreateLogger<Anthropic>())]
            });

        // The decorator is what the framework talks to: it keeps requests inside Claude's API rules and marks the system prompt for caching.
        return new MorganaAnthropicClient(anthropicClient.AsIChatClient(options.ModelId), loggerFactory);
    }

    /// <summary>
    /// Writes down every call actually put on the wire, including the ones the SDK retries by itself.
    /// </summary>
    /// <remarks>
    /// The telemetry wrapper above measures a completion end to end, so a call throttled three times
    /// and answered on the fourth reads there as one slow call. This is the only place that can say
    /// it was throttled: a refusal carrying a retry-after is recorded as a refusal, so a wait nobody
    /// asked for stops looking like a model taking its time.
    /// </remarks>
    private sealed class AttemptLogger(ILogger? logger) : DelegatingHandler
    {
        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            // The clock covers the single attempt on the wire so that the log can tell a slow answer from a throttled one.
            long startedAt = Stopwatch.GetTimestamp();

            // The attempt goes out on the wire and the answer is kept for the timing that follows.
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);

            // The time the attempt took is measured, so the log can tell a slow answer from a throttled one.
            double elapsed = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;

            // A throttled or overloaded answer is a warning because the SDK is about to wait and retry; any other answer is routine.
            if (response.StatusCode is HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError)
                logger?.LogWarning(
                    "Anthropic refused a call with {Status} after {Elapsed:0.0}s and asks to wait {RetryAfter}s; the SDK will retry it",
                    (int)response.StatusCode, elapsed,
                    response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) ?? "an unstated number of");
            else
                logger?.LogInformation("Anthropic answered {Status} in {Elapsed:0.0}s", (int)response.StatusCode, elapsed);

            // The response travels on untouched: the handler only observes the attempt.
            return response;
        }
    }
}