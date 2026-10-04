using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Morgana.Contracts;

namespace Morgana.Terminal.Services;

/// <summary>
/// Thin REST client wrapping the Morgana conversation lifecycle endpoints
/// (<c>/api/morgana/conversation/start</c>, <c>.../message</c>, <c>.../command</c>, <c>.../end</c>), the command catalogue and the health check.
/// Relies on <see cref="IHttpClientFactory"/>'s named <c>Morgana</c> client, which
/// is wired with the per-issuer JWT <see cref="Handlers.MorganaAuthHandler"/>.
/// </summary>
public sealed class MorganaClientService
{
    /// <summary>Fallback for the channel's <c>CommandTimeoutSeconds</c> when absent or non-positive.</summary>
    private const int DefaultCommandTimeoutSeconds = 120;

    /// <summary>Produces the named <c>Morgana</c> <see cref="HttpClient"/> with the JWT handler already wired in.</summary>
    private readonly IHttpClientFactory httpClientFactory;

    /// <summary>Identity and capability budget announced on every handshake.</summary>
    private readonly ChannelProfile profile;

    /// <summary>Absolute URL Morgana POSTs inbound messages to; re-announced on every handshake.</summary>
    private readonly string callbackUrl;

    /// <summary>
    /// How long the prompt waits on one of Morgana's commands before it is called off. The channel's own
    /// deadline, never sent: this side knows how long its prompt can be held, while Morgana only notices
    /// the call being dropped. From the channel's <c>CommandTimeoutSeconds</c>.
    /// </summary>
    private readonly TimeSpan commandTimeout;

    /// <summary>Captures the callback URL (required).</summary>
    /// <exception cref="InvalidOperationException">Thrown when the channel's <c>CallbackURL</c> is missing.</exception>
    public MorganaClientService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ChannelProfile profile)
    {
        this.httpClientFactory = httpClientFactory;
        this.profile = profile;
        callbackUrl = configuration[profile.SectionKey("CallbackURL")]
            ?? throw new InvalidOperationException($"{profile.SectionKey("CallbackURL")} is required for webhook-based delivery.");

        // A non-positive wait would call every command off the instant it is sent, so it falls back too
        int commandTimeoutSeconds = configuration.GetValue<int?>(profile.SectionKey("CommandTimeoutSeconds")) ?? DefaultCommandTimeoutSeconds;
        commandTimeout = TimeSpan.FromSeconds(commandTimeoutSeconds > 0 ? commandTimeoutSeconds : DefaultCommandTimeoutSeconds);
    }

    /// <summary>How long a health check waits before calling Morgana unreachable: long enough for a busy host, short enough to wait on.</summary>
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The URL Morgana delivers this channel's replies to, as announced on every handshake.</summary>
    public string CallbackUrl => callbackUrl;

    /// <summary>Morgana's base address as this channel is configured to reach it.</summary>
    public Uri? MorganaAddress => httpClientFactory.CreateClient("Morgana").BaseAddress;

    /// <summary>Tells whether Morgana answers its health check as healthy; false when it is down, unreachable or too slow to answer.</summary>
    public async Task<bool> IsMorganaHealthyAsync(CancellationToken cancellationToken = default)
    {
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(HealthCheckTimeout);
        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync("/api/morgana/health", deadline.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A refused connection and a host that outlasts the deadline both mean the same thing to the user
            return false;
        }
    }

    /// <summary>
    /// Opens a new conversation with Morgana, declaring the channel's handshake: its name, the
    /// webhook delivery mode, the capability profile it can render and the callback URL.
    /// </summary>
    /// <param name="candidateConversationId">The id proposed to Morgana; the server is source of truth, so the channel uses the one returned.</param>
    /// <param name="cancellationToken">Abandons the handshake when the process is stopping.</param>
    public async Task<string> StartConversationAsync(string candidateConversationId, CancellationToken cancellationToken = default)
    {
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");

        StartConversationRequest body = new(
            ConversationId: candidateConversationId,
            ChannelMetadata: profile.BuildMetadata(callbackUrl));

        HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            "/api/morgana/conversation/start", body, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Fail-closed: a 2xx with an empty/missing id means the contract is broken —
        // refuse to run rather than leak an undefined conversation into the webhook loop.
        StartConversationResponse? parsed = await response.Content.ReadFromJsonAsync<StartConversationResponse>(cancellationToken);
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.ConversationId))
            throw new InvalidOperationException("Morgana did not return a conversation id.");
        return parsed.ConversationId;
    }

    /// <summary>Sends a user message on the given conversation.</summary>
    public async Task SendMessageAsync(string conversationId, string text, CancellationToken cancellationToken = default)
    {
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");
        HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            $"/api/morgana/conversation/{conversationId}/message",
            new SendMessageRequest(text),
            cancellationToken);

        // 429 (rate-limit OR dust exhaustion) is not a transport failure: before returning
        // it the backend has already pushed a user-facing explanatory ChannelMessage over
        // the webhook, which the UI renders with its own terminal styling. Letting
        // EnsureSuccessStatusCode throw here would surface a raw "send failed: 429" line that
        // buries that message and reads like a crash. Swallow it and let the channel speak —
        // same contract Cauldron honours. Any other non-success still throws so genuine
        // failures stay visible.
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            return;

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Reads the commands Morgana publishes, for the palette to list next to the channel's own.</summary>
    public async Task<IReadOnlyList<CommandDescriptor>> GetCommandCatalogAsync(CancellationToken cancellationToken = default)
    {
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");
        CommandCatalogResponse? catalog = await httpClient.GetFromJsonAsync<CommandCatalogResponse>(
            "/api/morgana/commands", cancellationToken);

        // An empty body reads as no published command, not as a broken contract: the palette still has the local ones
        return catalog?.Commands ?? [];
    }

    /// <summary>Reads the conversation as Morgana has it on record, in chronological order; empty when it holds nothing yet.</summary>
    public async Task<IReadOnlyList<MorganaChatMessage>> GetHistoryAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");
        HttpResponseMessage response = await httpClient.GetAsync($"/api/morgana/conversation/{conversationId}/history", cancellationToken);

        // A conversation nobody has spoken in yet is reported as absent, which is not a failure to report upwards
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];

        response.EnsureSuccessStatusCode();
        ConversationHistoryResponse? history = await response.Content.ReadFromJsonAsync<ConversationHistoryResponse>(cancellationToken);
        return history?.Messages ?? [];
    }

    /// <summary>
    /// Runs one of Morgana's published commands on the given conversation, returning once Morgana has finished
    /// it; its progress and outcome arrive over the webhook, each frame carrying <paramref name="invocationId"/>.
    /// Morgana refuses a command asking to be confirmed unless <paramref name="confirmed"/> carries the user's Yes.
    /// </summary>
    /// <exception cref="TimeoutException">Thrown when the command outlives the channel's command deadline, which calls it off on Morgana too.</exception>
    public async Task RunCommandAsync(string conversationId, string name, string invocationId, IReadOnlyDictionary<string, string>? options = null, bool confirmed = false, CancellationToken cancellationToken = default)
    {
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");

        // The command deadline alone decides how long this call may last: the client's own default would cut
        // a command short of it and be reported as a transport failure
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        using CancellationTokenSource commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandDeadline.CancelAfter(commandTimeout);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.PostAsJsonAsync(
                $"/api/morgana/conversation/{conversationId}/command",
                new ExecuteCommandRequest(name, confirmed, options, invocationId),
                commandDeadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Dropping the call is what tells Morgana to stop. A command called off that way writes nothing:
            // the user reads the deadline as the outcome, since no other one will arrive
            throw new TimeoutException($"did not finish within {commandTimeout.TotalSeconds:0}s and was called off");
        }

        // A command meets the limits a message meets: Morgana explains a 429 over the webhook just the same
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            return;

        response.EnsureSuccessStatusCode();
    }

    /// <summary>Terminates the conversation server-side; best-effort (swallows errors on shutdown).</summary>
    public async Task EndConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        try
        {
            HttpClient httpClient = httpClientFactory.CreateClient("Morgana");
            await httpClient.PostAsync($"/api/morgana/conversation/{conversationId}/end",
                content: null, cancellationToken);
        }
        catch
        {
            // Best-effort on shutdown — if Morgana is already gone or unreachable, we don't care.
        }
    }
}
