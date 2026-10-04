using Microsoft.Extensions.Configuration;
using Morgana.Terminal.Interfaces;
using Spectre.Console;

namespace Morgana.Terminal.Services;

/// <summary>
/// Drives a TTY channel process from its first conversation to its exit: opens the conversation with
/// Morgana, waits for the presentation, hands the terminal to the live UI and ends whichever conversation
/// is on screen when the UI returns, since <c>/new</c> may have replaced the first one meanwhile.
/// </summary>
public sealed class ConversationLifecycleService
{
    /// <summary>Fallback for the channel's <c>StartupTimeoutSeconds</c> when absent or non-positive.</summary>
    private const int DefaultStartupTimeoutSeconds = 30;

    /// <summary>Ends the conversation on Morgana's REST endpoints.</summary>
    private readonly MorganaClientService morganaClientService;

    /// <summary>Opens the conversation and names the one on screen.</summary>
    private readonly TerminalSessionService session;

    /// <summary>Joins Morgana's published commands to the palette once the conversation is open.</summary>
    private readonly TerminalCommandRegistryService commandRegistry;

    /// <summary>Paces the attempts to open the conversation while Morgana is unreachable.</summary>
    private readonly MorganaStartRetryPolicy startRetryPolicy;

    /// <summary>Where Morgana's deliveries land, routed from here to the UI.</summary>
    private readonly WebhookReceiverService webhookReceiverService;

    /// <summary>The live terminal UI the conversation is handed to once Morgana has spoken.</summary>
    private readonly ITerminalUi terminalUi;

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
        TerminalSessionService session,
        TerminalCommandRegistryService commandRegistry,
        MorganaStartRetryPolicy startRetryPolicy,
        WebhookReceiverService webhookReceiverService,
        ITerminalUi terminalUi,
        LandingMessageService landingMessageService,
        IConfiguration configuration,
        ChannelProfile profile)
    {
        this.morganaClientService = morganaClientService;
        this.session = session;
        this.commandRegistry = commandRegistry;
        this.startRetryPolicy = startRetryPolicy;
        this.webhookReceiverService = webhookReceiverService;
        this.terminalUi = terminalUi;
        this.landingMessageService = landingMessageService;

        int startupTimeoutSeconds = configuration.GetValue<int?>(profile.SectionKey("StartupTimeoutSeconds")) ?? DefaultStartupTimeoutSeconds;
        startupTimeout = TimeSpan.FromSeconds(startupTimeoutSeconds > 0 ? startupTimeoutSeconds : DefaultStartupTimeoutSeconds);
    }

    /// <summary>
    /// Runs the conversation from handshake to end. Returns when the user quits or <paramref name="stopping"/>
    /// fires; at once when the conversation could not be opened.
    /// Requires the webhook listener to be already accepting Morgana's deliveries.
    /// </summary>
    public async Task RunAsync(CancellationToken stopping)
    {
        // Something alive on screen while the handshake and the presentation are on their way
        AnsiConsole.MarkupLine($"[italic grey70]{Markup.Escape(landingMessageService.GetLandingMessage())}[/]");

        Task presentationArrived = RouteWebhookToUi();

        if (!await OpenConversationAsync(stopping))
            return;

        try
        {
            // Morgana's commands join the palette whenever the catalogue answers: a slow one must not hold the UI back
            _ = commandRegistry.LoadMorganaCatalogAsync(stopping);
            await WaitForPresentationAsync(presentationArrived);

            // The live display takes over a clean terminal, otherwise the landing line would stay orphaned above it
            AnsiConsole.Clear();

            // The host's stopping token tears the UI down on SIGTERM, SIGINT and SIGHUP, so the process
            // can exit and docker's --rm reclaims the container
            await terminalUi.RunAsync(session, stopping);
        }
        finally
        {
            // Whichever conversation is on screen by now: /new may have replaced the one opened above
            await morganaClientService.EndConversationAsync(session.ConversationId);
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
            terminalUi.EnqueueIncoming(message);
        };

        // A channel that renders no chunks leaves the sink unset: the receiver then turns them away
        webhookReceiverService.OnChunk = terminalUi.ChunkSink;
        return firstMessageArrived.Task;
    }

    /// <summary>
    /// Opens the first conversation, returning whether it could be. A Morgana not up yet
    /// or being redeployed is waited for at the retry policy's pace until the user quits; only a refusal
    /// that would repeat forever, such as a rejected key, is reported at once.
    /// </summary>
    private async Task<bool> OpenConversationAsync(CancellationToken stopping)
    {
        for (int failedAttempts = 0; ; failedAttempts++)
        {
            try
            {
                await session.OpenConversationAsync(stopping);
                return true;
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return false;
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
                    return false;
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Failed to open conversation with Morgana:[/] {Markup.Escape(ex.Message)}");
                return false;
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
