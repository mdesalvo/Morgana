using Spectre.Console;

namespace Rune.Services;

/// <summary>
/// Drives the one conversation a Rune process lives for: opens it with Morgana, waits for the
/// presentation, hands the terminal to the live UI and ends the conversation when the UI returns.
/// </summary>
public sealed class ConversationLifecycleService
{
    /// <summary>Fallback for <c>Rune:StartupTimeoutSeconds</c> when absent or non-positive.</summary>
    private const int DefaultStartupTimeoutSeconds = 30;

    /// <summary>Opens, feeds and ends the conversation on Morgana's REST endpoints.</summary>
    private readonly MorganaClientService morganaClientService;

    /// <summary>Paces the attempts to open the conversation while Morgana is unreachable.</summary>
    private readonly MorganaStartRetryPolicy startRetryPolicy;

    /// <summary>Where Morgana's deliveries land, routed from here to the UI.</summary>
    private readonly WebhookReceiverService webhookReceiverService;

    /// <summary>The live terminal UI the conversation is handed to once Morgana has spoken.</summary>
    private readonly ConsoleUiService consoleUiService;

    /// <summary>Supplies the line shown while the handshake is under way.</summary>
    private readonly LandingMessageService landingMessageService;

    /// <summary>
    /// How long to wait for Morgana's presentation before entering the UI anyway. Raising it helps on
    /// providers with cold starts; a non-positive value falls back to the default, since a zero wait
    /// would show an empty conversation every time.
    /// </summary>
    private readonly TimeSpan startupTimeout;

    /// <summary>Captures the collaborators and reads the startup timeout from configuration.</summary>
    public ConversationLifecycleService(
        MorganaClientService morganaClientService,
        MorganaStartRetryPolicy startRetryPolicy,
        WebhookReceiverService webhookReceiverService,
        ConsoleUiService consoleUiService,
        LandingMessageService landingMessageService,
        IConfiguration configuration)
    {
        this.morganaClientService = morganaClientService;
        this.startRetryPolicy = startRetryPolicy;
        this.webhookReceiverService = webhookReceiverService;
        this.consoleUiService = consoleUiService;
        this.landingMessageService = landingMessageService;

        int startupTimeoutSeconds = configuration.GetValue<int?>("Rune:StartupTimeoutSeconds") ?? DefaultStartupTimeoutSeconds;
        startupTimeout = TimeSpan.FromSeconds(startupTimeoutSeconds > 0 ? startupTimeoutSeconds : DefaultStartupTimeoutSeconds);
    }

    /// <summary>
    /// Runs the conversation from handshake to end. Returns when the user quits or <paramref name="stopping"/>
    /// fires, and at once when the conversation could not be opened.
    /// Requires the webhook listener to be already accepting Morgana's deliveries.
    /// </summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        // Something alive on screen while the handshake and the presentation are on their way
        AnsiConsole.MarkupLine($"[italic grey70]{Markup.Escape(landingMessageService.GetLandingMessage())}[/]");

        Task presentationArrived = RouteWebhookToUi();

        string? conversationId = await OpenConversationAsync(stopping);
        if (conversationId is null)
            return;

        try
        {
            await WaitForPresentationAsync(presentationArrived);

            // The live display takes over a clean terminal, otherwise the landing line would stay orphaned above it
            AnsiConsole.Clear();

            // The host's stopping token tears the UI down on SIGTERM, SIGINT and SIGHUP, so the process
            // can exit and docker's --rm reclaims the container
            await consoleUiService.RunAsync(
                conversationId,
                text => morganaClientService.SendMessageAsync(conversationId, text),
                stopping);
        }
        finally
        {
            await morganaClientService.EndConversationAsync(conversationId);
        }
    }

    /// <summary>
    /// Sends every delivery from Morgana to the UI queue. The returned task completes on the first
    /// message, which gates the handover: entering the live display as soon as the handshake returns
    /// would show an empty panel while the presentation is still in flight.
    /// </summary>
    private Task RouteWebhookToUi()
    {
        TaskCompletionSource firstMessageArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        webhookReceiverService.OnMessage = message =>
        {
            firstMessageArrived.TrySetResult();
            consoleUiService.EnqueueIncoming(message);
        };
        return firstMessageArrived.Task;
    }

    /// <summary>
    /// Opens the conversation, returning its id or null when it could not be opened. A Morgana not up yet
    /// or being redeployed is waited for at the retry policy's pace until the user quits; only a refusal
    /// that would repeat forever, such as a rejected key, is reported at once.
    /// </summary>
    private async Task<string?> OpenConversationAsync(CancellationToken stopping)
    {
        for (int failedAttempts = 0; ; failedAttempts++)
        {
            // Accepted before the handshake is sent, since the presentation may land before its reply.
            // Each attempt proposes a fresh id, so what an abandoned attempt still delivers is refused.
            string candidateConversationId = Guid.NewGuid().ToString("N");
            webhookReceiverService.ExpectedConversationId = candidateConversationId;
            try
            {
                string conversationId = await morganaClientService.StartConversationAsync(candidateConversationId, stopping);
                webhookReceiverService.ExpectedConversationId = conversationId;
                return conversationId;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex) when (MorganaStartRetryPolicy.IsWorthRetrying(ex))
            {
                TimeSpan retryDelay = startRetryPolicy.NextRetryDelay(failedAttempts);
                AnsiConsole.MarkupLine($"[grey70]Morgana is not answering ({Markup.Escape(ex.Message)}): trying again in {retryDelay.TotalSeconds:0}s, Ctrl+C to quit[/]");
                try
                {
                    await Task.Delay(retryDelay, stopping);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to open conversation with Morgana:[/] {Markup.Escape(ex.Message)}");
                return null;
            }
        }
    }

    /// <summary>
    /// Waits for the presentation within <see cref="startupTimeout"/>. Past it the UI is entered anyway:
    /// an empty conversation is a better place to be than a terminal stuck on the landing line.
    /// </summary>
    private async Task WaitForPresentationAsync(Task presentationArrived)
    {
        try
        {
            await presentationArrived.WaitAsync(startupTimeout);
        }
        catch (TimeoutException)
        {
            // Morgana is late: the presentation will land in the UI whenever it arrives
        }
    }
}
