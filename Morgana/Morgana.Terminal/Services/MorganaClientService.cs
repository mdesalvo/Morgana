using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Morgana.Contracts;

namespace Morgana.Terminal.Services;

/// <summary>
/// The channel's REST client for Morgana: the conversation's start, messages, commands, history and end, the command catalogue and the health check.
/// Every call goes through the factory's named Morgana client, which carries the channel's MorganaAuthHandler.
/// </summary>
public sealed class MorganaClientService
{
    /// <summary>The fallback for the channel's <c>CommandTimeoutSeconds</c> when it is absent or not positive.</summary>
    private const int DefaultCommandTimeoutSeconds = 120;

    /// <summary>Produces the named <c>Morgana</c> <see cref="HttpClient"/> with the JWT handler already wired in.</summary>
    private readonly IHttpClientFactory httpClientFactory;

    /// <summary>The identity and capability budget announced on every handshake.</summary>
    private readonly ChannelProfile profile;

    /// <summary>The absolute URL Morgana POSTs inbound messages to, announced again on every handshake.</summary>
    private readonly string callbackUrl;

    /// <summary>
    /// How long the prompt waits on one of Morgana's commands before calling it off, from the channel's <c>CommandTimeoutSeconds</c>.
    /// The channel's own deadline, never sent: this side knows how long its prompt can be held, while Morgana only notices the call being dropped.
    /// </summary>
    private readonly TimeSpan commandTimeout;

    /// <summary>Builds the client on the channel's configuration, reading its callback URL and its command deadline.</summary>
    /// <exception cref="InvalidOperationException">The channel's <c>CallbackURL</c> is missing.</exception>
    public MorganaClientService(IHttpClientFactory httpClientFactory, IConfiguration configuration, ChannelProfile profile)
    {
        // Every call is made through the factory and announces this channel's profile.
        this.httpClientFactory = httpClientFactory;
        this.profile = profile;

        // A webhook channel without a callback has nowhere for Morgana's replies to land.
        callbackUrl = configuration[profile.SectionKey("CallbackURL")]
            ?? throw new InvalidOperationException($"{profile.SectionKey("CallbackURL")} is required for webhook-based delivery.");

        // A non-positive wait would call every command off the instant it is sent, so it falls back too
        int commandTimeoutSeconds = configuration.GetValue<int?>(profile.SectionKey("CommandTimeoutSeconds")) ?? DefaultCommandTimeoutSeconds;
        commandTimeout = TimeSpan.FromSeconds(commandTimeoutSeconds > 0 ? commandTimeoutSeconds : DefaultCommandTimeoutSeconds);
    }

    /// <summary>How long a health check waits before calling Morgana unreachable, long enough for a busy host and short enough to wait on.</summary>
    private static readonly TimeSpan HealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The URL Morgana delivers this channel's replies to, as announced on every handshake.</summary>
    public string CallbackUrl => callbackUrl;

    /// <summary>Morgana's base address as this channel is configured to reach it.</summary>
    public Uri? MorganaAddress => httpClientFactory.CreateClient("Morgana").BaseAddress;

    /// <summary>Tells whether Morgana answers its health check as healthy; false when it is down, unreachable or too slow to answer.</summary>
    public async Task<bool> IsMorganaHealthyAsync(CancellationToken cancellationToken = default)
    {
        // The check gives up at its own deadline, apart from the caller's cancellation.
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(HealthCheckTimeout);

        try
        {
            // Any success status is Morgana answering as healthy.
            using HttpResponseMessage response = await httpClient.GetAsync("/api/morgana/health", deadline.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // A refused connection and a host that outlasts the deadline both mean the same thing to the user
            return false;
        }
    }

    /// <summary>Opens a new conversation with Morgana by declaring the channel's handshake and returns the id Morgana opened it under.</summary>
    /// <param name="candidateConversationId">The id proposed to Morgana; Morgana is the source of truth, so the channel uses the one returned.</param>
    /// <param name="cancellationToken">Abandons the handshake when the process is stopping.</param>
    public async Task<string> StartConversationAsync(string candidateConversationId, CancellationToken cancellationToken = default)
    {
        // The client carries the channel's token.
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");

        // The handshake proposes the id and declares what the channel is and can render
        StartConversationRequest body = new(
            ConversationId: candidateConversationId,
            ChannelMetadata: profile.BuildMetadata(callbackUrl));

        // Morgana answers with the conversation it opened
        HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            "/api/morgana/conversation/start", body, cancellationToken);
        response.EnsureSuccessStatusCode();

        // Fail-closed: a success with no id means the contract is broken, so the channel refuses to run rather than leak an
        // undefined conversation into the webhook loop.
        StartConversationResponse? startResponse = await response.Content.ReadFromJsonAsync<StartConversationResponse>(cancellationToken);
        if (startResponse is null || string.IsNullOrWhiteSpace(startResponse.ConversationId))
            throw new InvalidOperationException("Morgana did not return a conversation id.");

        // The conversation every later call and every delivery is addressed to.
        return startResponse.ConversationId;
    }

    /// <summary>Sends a user message on the given conversation.</summary>
    public async Task SendMessageAsync(string conversationId, string text, CancellationToken cancellationToken = default)
    {
        // The reply arrives over the webhook, so the call only hands the message over.
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");
        HttpResponseMessage response = await httpClient.PostAsJsonAsync(
            $"/api/morgana/conversation/{conversationId}/message",
            new SendMessageRequest(text),
            cancellationToken);

        // A 429, a rate limit or a spent budget, is no transport failure: Morgana has already pushed the notice explaining it
        // over the webhook, which a raw "send failed: 429" would bury under what reads like a crash. Cauldron honours the same
        // contract.
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            return;

        // A refused message is a failure to show; a 429 returned above is the one exception
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Reads the commands Morgana publishes, for the palette to list next to the channel's own.</summary>
    public async Task<IReadOnlyList<CommandDescriptor>> GetCommandCatalogAsync(CancellationToken cancellationToken = default)
    {
        // The catalogue is the same for every conversation, so no conversation is named.
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");
        CommandCatalogResponse? catalog = await httpClient.GetFromJsonAsync<CommandCatalogResponse>(
            "/api/morgana/commands", cancellationToken);

        // An empty body reads as no published command, not as a broken contract: the palette still has the local ones
        return catalog?.Commands ?? [];
    }

    /// <summary>Reads the conversation as Morgana has it on record, in chronological order; empty when it holds nothing yet.</summary>
    public async Task<IReadOnlyList<MorganaChatMessage>> GetHistoryAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        // The record is read from Morgana, not from the transcript on screen.
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");
        HttpResponseMessage response = await httpClient.GetAsync($"/api/morgana/conversation/{conversationId}/history", cancellationToken);

        // A conversation Morgana does not know has nothing on record, which is not a failure to report upwards
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];

        // A refusal other than 404 is a failure to show
        response.EnsureSuccessStatusCode();

        // A started conversation in which nobody has spoken yet answers with an empty transcript.
        ConversationHistoryResponse? history = await response.Content.ReadFromJsonAsync<ConversationHistoryResponse>(cancellationToken);
        return history?.Messages ?? [];
    }

    /// <summary>
    /// Runs one of Morgana's published commands on the conversation, returning once Morgana has finished it.
    /// Its progress and outcome arrive over the webhook, each frame carrying invocationId. Morgana refuses a command asking to be
    /// confirmed unless confirmed carries the user's Yes.
    /// </summary>
    /// <exception cref="TimeoutException">The command outlived the channel's command deadline, which calls it off on Morgana too.</exception>
    public async Task RunCommandAsync(string conversationId, string name, string invocationId, IReadOnlyDictionary<string, string>? options = null, bool confirmed = false, CancellationToken cancellationToken = default)
    {
        // The client carries the channel's token.
        HttpClient httpClient = httpClientFactory.CreateClient("Morgana");

        // The command deadline alone decides how long this call may last: the client's own default would cut
        // a command short of it and be reported as a transport failure
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        using CancellationTokenSource commandDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        commandDeadline.CancelAfter(commandTimeout);

        // The answer to the command; the deadline that called it off throws instead
        HttpResponseMessage response;
        try
        {
            // Morgana runs the command and answers when it has finished
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

        // A refusal other than 429 is a failure to show
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Ends the conversation on Morgana's side, as a best effort that never throws.</summary>
    public async Task EndConversationAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        try
        {
            // The client is pooled by the factory and carries the channel's token
            HttpClient httpClient = httpClientFactory.CreateClient("Morgana");
            await httpClient.PostAsync($"/api/morgana/conversation/{conversationId}/end",
                content: null, cancellationToken);
        }
        catch
        {
            // A Morgana already gone or unreachable has nothing left to end, so the failure is not the user's concern.
        }
    }
}