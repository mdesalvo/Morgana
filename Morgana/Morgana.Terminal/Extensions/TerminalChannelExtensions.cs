using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Morgana.Contracts;
using Morgana.Terminal.Handlers;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Messages;
using Morgana.Terminal.Services;
using Spectre.Console;
using System.Text.Json;

namespace Morgana.Terminal.Extensions;

/// <summary>What every TTY channel host does the same way: refuse to start where it cannot work, wire the shared services and map the webhook.</summary>
public static class TerminalChannelExtensions
{
    /// <summary>The shipped value of the signing key, a marker and not a key.</summary>
    private const string SigningKeyMarker = "_SECURE_OVERRIDE_";

    /// <summary>
    /// Prepares the process and tells whether the channel may start, explaining on the console why not. The culture and
    /// the console encoding are set first. The terminal must be interactive and the
    /// three settings the channel cannot run without must be sound: they are checked together so a fresh checkout is
    /// told exactly what is missing while there is still a readable terminal.
    /// </summary>
    public static bool CanStartTerminalChannel(this WebApplicationBuilder builder, ChannelProfile profile)
    {
        // Invariant before anything else runs, so no thread inherits the host's locale: the terminal and
        // every export read the same on a workstation set to it-IT and in a container with no LANG at all
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

        // On Windows the default OutputEncoding is the OEM/ANSI code page (CP437/CP850/CP1252), none of which covers
        // the BMP glyphs the channel renders: the header arrow, the prompt chevron, the ellipsis and the courtesy dashes.
        // Under those code pages the console falls back to a glyph that looks like a tofu box. Forcing both streams
        // to UTF-8 is idempotent on Linux/macOS and spares the user a chcp 65001
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        // Spectre's live UI requires a real PTY: ANSI escape support and a blocking Console.ReadKey. Non-interactive
        // hosts (IDE run windows, CI, redirected or piped output) capture stdout but expose none, so Spectre silently
        // refuses to render and the user sees an empty screen. Failing here gives an actionable message instead
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            // The user is told how to get a terminal that works
            Console.WriteLine($"{profile.DisplayName} requires an interactive TTY but the current output stream is not one. Launch it from a real terminal emulator (bash, zsh, pwsh, ...); if you are running it from an IDE, enable the equivalent of 'emulate terminal' on the run configuration.");
            return false;
        }

        // Backend address, callback address and signing key are each fatal on their own: without them the channel can
        // neither reach Morgana, be reached back, nor be trusted. Once the live UI owns the terminal, with logging
        // silenced, the same omission would surface as a bare stack trace
        string?[] requiredSettings =
        [
            IsReachableUrl(builder.Configuration[profile.SectionKey("MorganaURL")])
                ? null
                : $"{profile.SectionKey("MorganaURL")} must be Morgana's absolute http(s) base URL (for example https://localhost:5001).",
            IsReachableUrl(builder.Configuration[profile.SectionKey("CallbackURL")])
                ? null
                : $"{profile.SectionKey("CallbackURL")} is required for webhook-based delivery and must be the absolute http(s) URL Morgana posts replies to (for example https://localhost:<port>/morgana-hook).",

            // The shipped value is a marker, not a key: left in place it buys a 401 from Morgana one
            // handshake later, where the reason is far less obvious than it is here
            builder.Configuration[profile.SectionKey("Authentication:SymmetricKey")] is not { Length: > 0 } signingKey || signingKey == SigningKeyMarker
                ? $"{profile.SectionKey("Authentication:SymmetricKey")} is required and must match Morgana's Authentication:Issuers entry for Name={profile.ChannelName}. Supply it through user-secrets or an environment variable."
                : null
        ];

        // Every problem is reported at once
        if (requiredSettings.Any(problem => problem is not null))
        {
            // The heading tells that the list below is what is wrong
            Console.WriteLine($"{profile.DisplayName} cannot start with the configuration it was given:");
            foreach (string problem in requiredSettings.OfType<string>())
                Console.WriteLine($"  - {problem}");
            return false;
        }

        // The terminal and the configuration are sound
        return true;
    }

    /// <summary>
    /// Registers everything a TTY channel is made of: its profile and theme, the self-issued JWT toward Morgana, the
    /// shared services, the UI and the resize watcher of the platform. A channel that registered its own
    /// <see cref="IMessageRenderer"/> beforehand keeps it, otherwise messages are drawn as plain text.
    /// </summary>
    public static WebApplicationBuilder AddTerminalChannel(this WebApplicationBuilder builder, ChannelProfile profile, TerminalTheme theme)
    {
        // The live UI owns the terminal: any stray log line from Kestrel or ASP.NET Core would corrupt the layout.
        // Errors still surface through explicit UI messages such as the red notice of a failed send
        builder.Logging.ClearProviders();

        // The profile is the single statement of who the channel is and what it can render: the shared services read
        // the handshake, the token claims and the configuration root from here
        builder.Services.AddSingleton(profile);
        builder.Services.AddSingleton(theme);

        // The handler self-issues a short-lived JWT on every request. The named client targets Morgana's base address
        // and runs through it, so the Authorization header is set without the caller knowing
        builder.Services.AddTransient<MorganaAuthHandler>();
        builder.Services.AddHttpClient("Morgana", client =>
        {
            // Morgana's base address comes from the channel's own configuration root
            client.BaseAddress = new Uri(builder.Configuration[profile.SectionKey("MorganaURL")]!);
        }).AddHttpMessageHandler<MorganaAuthHandler>();

        // One channel is one process and one session: every service is a singleton
        builder.Services.AddSingleton<MorganaClientService>();
        builder.Services.AddSingleton<TerminalSessionService>();
        builder.Services.AddSingleton<TerminalCommandRegistryService>();
        builder.Services.AddSingleton<CommandPaletteService>();
        builder.Services.AddSingleton<CommandConfirmationService>();
        builder.Services.AddSingleton<CommandProgressService>();
        builder.Services.AddSingleton<CommandOptionPromptService>();
        builder.Services.AddSingleton<QuickReplyPromptService>();
        builder.Services.AddSingleton<StreamingPaneService>();
        builder.Services.AddSingleton<MorganaStartRetryPolicy>();
        builder.Services.AddSingleton<ConversationLifecycleService>();
        builder.Services.AddSingleton<WebhookReceiverService>();
        builder.Services.AddSingleton<LandingMessageService>();
        builder.Services.AddSingleton<TerminalCellService>();
        builder.Services.AddSingleton<ITerminalUi, TerminalUiService>();

        // A channel that renders more than plain text registers its renderer before this call and keeps it
        builder.Services.TryAddSingleton<IMessageRenderer, PlainTextMessageRenderService>();

        // The UI needs to know when the user resizes the window. POSIX delivers SIGWINCH at zero idle cost;
        // Windows has no equivalent at this layer and falls back to a 250 ms polling comparison
        if (OperatingSystem.IsWindows())
            builder.Services.AddSingleton<IViewportResizeWatcher, PollingResizeWatcherService>();
        else
            builder.Services.AddSingleton<IViewportResizeWatcher, SigWinchResizeWatcherService>();

        // The builder, so the registrations can be chained
        return builder;
    }

    /// <summary>
    /// Maps the endpoints Morgana delivers to: <c>/morgana-hook</c> for messages and, only where the profile declares
    /// streaming, <c>/morgana-hook/chunk</c> for the incremental deltas. No JWT guards them, since the trust model is
    /// asymmetric by design, as in <c>WebhookChannelService</c>. A delivery for another conversation is refused with 404.
    /// </summary>
    public static WebApplication MapMorganaHooks(this WebApplication app)
    {
        ChannelProfile profile = app.Services.GetRequiredService<ChannelProfile>();

        // Morgana POSTs a serialized message on every outbound turn
        app.MapPost("/morgana-hook", async (HttpContext httpContext, WebhookReceiverService receiverService) =>
        {
            // The delivery as Morgana serialized it
            ChannelMessage? message;
            try
            {
                // The body is bound to the contract Morgana and the channels share
                message = await httpContext.Request.ReadFromJsonAsync<ChannelMessage>();
            }
            catch (JsonException)
            {
                // A body that is not JSON of that shape is refused
                return Results.BadRequest();
            }

            // A body that is not a message is refused
            if (message is null)
                return Results.BadRequest();

            // A delivery for another conversation is refused, so Morgana logs the misdelivery on its side
            return receiverService.Dispatch(message) ? Results.Ok() : Results.NotFound();
        });

        // A channel that declares no streaming has no chunk endpoint: Morgana never sends it any
        if (!profile.Capabilities.SupportsStreaming)
            return app;

        // Counterpart of WebhookChannelService.SendStreamChunkAsync: each POST carries an incremental delta of an
        // agent's response, revealed live and committed when the final message lands
        app.MapPost("/morgana-hook/chunk", async (HttpContext httpContext, WebhookReceiverService receiverService) =>
        {
            // The delta as Morgana serialized it
            StreamChunkRequest? chunk;
            try
            {
                // The body is bound to the contract Morgana and the channels share
                chunk = await httpContext.Request.ReadFromJsonAsync<StreamChunkRequest>();
            }
            catch (JsonException)
            {
                // A body that is not JSON of that shape is refused
                return Results.BadRequest();
            }

            // A body that is not a chunk is refused
            if (chunk is null)
                return Results.BadRequest();

            // A chunk for another conversation is refused like a message
            return receiverService.DispatchChunk(chunk) ? Results.Ok() : Results.NotFound();
        });

        // The app, so the mappings can be chained
        return app;
    }

    /// <summary>
    /// Runs the channel from the first listener to the exit: Kestrel starts first so it is ready for Morgana's first
    /// delivery, then the conversation runs on this thread, which the live UI needs. Whatever way it ends, the SIGHUP
    /// hook is released and Kestrel stopped, so the process exits and docker's --rm reclaims the container.
    /// </summary>
    public static async Task RunTerminalChannelAsync(this WebApplication app)
    {
        // .NET's host already wires SIGTERM/SIGINT to ApplicationStopping on every platform. On POSIX, closing the host
        // terminal window also sends SIGHUP, which is not handled by default: without this registration the docker CLI
        // dies, the container keeps running, --rm never fires and `compose down` complains that morgana-network is
        // still in use. Windows has no mapping for it: closing the console is delivered as SIGTERM
        PosixSignalRegistration? sighupRegistration = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
            {
                // The default termination is cancelled so the host stops in order instead of dying
                context.Cancel = true;
                app.Lifetime.StopApplication();
            });

        // The listener is ready before the handshake tells Morgana where to deliver
        await app.StartAsync();
        try
        {
            // The conversation runs until the user quits or the host stops
            await app.Services.GetRequiredService<ConversationLifecycleService>().RunAsync(app.Lifetime.ApplicationStopping);
        }
        finally
        {
            // The hook is released and Kestrel stopped however the conversation ended
            sighupRegistration?.Dispose();
            await app.StopAsync();
        }
    }

    /// <summary>
    /// Tells whether <paramref name="value"/> is an address the channel can dial: anything else (a bare host:port,
    /// a path, a scheme nobody serves) is parsed happily and only fails once the first call is already on its way.
    /// </summary>
    private static bool IsReachableUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? url)
        && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);
}
