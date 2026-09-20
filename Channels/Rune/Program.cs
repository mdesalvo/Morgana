using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Morgana.Contracts;
using Morgana.Terminal;
using Morgana.Terminal.Handlers;
using Morgana.Terminal.Interfaces;
using Morgana.Terminal.Services;
using Rune.Messages;
using Rune.Services;
using Spectre.Console;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ==============================================================================
// RUNE - WEBHOOK CHANNEL FOR MORGANA
// ==============================================================================
// Rune is a minimal CLI reference channel talking to Morgana over the webhook
// delivery mode. It declares a tight capability budget (500-char hard limit, no
// rich cards, no quick replies, no streaming, no markdown) so Morgana's channel
// adapter rewrite path is exercised on every turn.
//
// Architecture: a Kestrel-hosted HTTPS listener on port 5003 receives inbound
// ChannelMessage payloads at POST /morgana-hook and drives a Spectre.Console
// terminal UI; user input is captured from stdin and sent back to Morgana via
// the REST conversation endpoints, authenticated with a self-issued JWT signed
// under iss=rune (the matching entry must live in Morgana's Authentication:Issuers).

// ==============================================================================
// 1. CONSOLE ENCODING - UTF-8 I/O
// ==============================================================================
// On Windows the default OutputEncoding is the OEM/ANSI code page (CP437/CP850/CP1252),
// none of which covers the BMP glyphs Rune renders: the header arrow (→ U+2192),
// the input prompt chevron (› U+203A), the conversation-id ellipsis (… U+2026) and
// the courtesy dashes. Under those code pages the console falls back to the "symbol
// for delete" glyph (␦), which looks like a tofu box to the user. Forcing both streams
// to UTF-8 up front is idempotent on Linux/macOS (already UTF-8) and makes Spectre's
// Unicode output render correctly on Windows without asking the user to chcp 65001.
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding  = Encoding.UTF8;

// ==============================================================================
// 2. TTY GATE - FAIL CLOSED ON NON-INTERACTIVE TERMINALS
// ==============================================================================
// Spectre.Console's Live UI requires a real PTY: ANSI escape support and a blocking
// Console.ReadKey. Non-interactive hosts (IDE run windows, CI, redirected or piped
// output, headless processes) capture stdout but don't expose one, so Spectre
// silently refuses to render and the user sees an empty screen. Detect this at
// startup and exit with an actionable message instead of a blank terminal.
if (!AnsiConsole.Profile.Capabilities.Interactive)
{
    Console.WriteLine("Rune requires an interactive TTY but the current output stream is not one. Launch it from a real terminal emulator (bash, zsh, pwsh, ...); if you are running it from an IDE, enable the equivalent of 'emulate terminal' on the run configuration.");
    return;
}

// ==============================================================================
// 2b. CONFIGURATION GATE - THE THREE SETTINGS RUNE CANNOT RUN WITHOUT
// ==============================================================================
// Backend address, callback address and signing key are each fatal on their own:
// without them Rune can neither reach Morgana, be reached back, nor be trusted.
// Checked together up front so a fresh checkout is told exactly what is missing
// while there is still a readable terminal — once the Live UI owns it, with
// logging silenced, the same omission would surface as a bare stack trace.
// MorganaURL must also be absolute, since it addresses another host.
// An address Rune can actually dial: anything else (a bare host:port, a path, a scheme nobody
// serves) is parsed happily and only fails once the first call is already on its way.
static bool IsReachableUrl(string? value) =>
    Uri.TryCreate(value, UriKind.Absolute, out Uri? url)
    && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

string?[] requiredSettings =
[
    IsReachableUrl(builder.Configuration["Rune:MorganaURL"])
        ? null
        : "Rune:MorganaURL must be Morgana's absolute http(s) base URL (for example https://localhost:5001).",
    IsReachableUrl(builder.Configuration["Rune:CallbackURL"])
        ? null
        : "Rune:CallbackURL is required for webhook-based delivery and must be the absolute http(s) URL Morgana posts replies to (for example https://localhost:5003/morgana-hook).",
    // The shipped value is a marker, not a key: left in place it buys a 401 from Morgana one
    // handshake later, where the reason is far less obvious than it is here
    builder.Configuration["Rune:Authentication:SymmetricKey"] is not { Length: > 0 } key || key == "_SECURE_OVERRIDE_"
        ? "Rune:Authentication:SymmetricKey is required and must match Morgana's Authentication:Issuers entry for Name=rune. Supply it through user-secrets or an environment variable."
        : null
];
if (requiredSettings.Any(problem => problem is not null))
{
    Console.WriteLine("Rune cannot start with the configuration it was given:");
    foreach (string problem in requiredSettings.OfType<string>())
        Console.WriteLine($"  - {problem}");
    return;
}

// ==============================================================================
// 3. LOGGING - KEEP THE TUI CLEAN
// ==============================================================================
// Spectre.Console renders over the terminal via Live, so any stray log line from
// Kestrel/ASP.NET Core corrupts the layout. Drop all providers; errors still
// surface through explicit UI messages (see ConsoleUiService's red system line).
builder.Logging.ClearProviders();

// ==============================================================================
// 4. OUTBOUND TO MORGANA - JWT + HTTP CLIENT
// ==============================================================================
// The channel profile is the single statement of who Rune is and what little it can render: the
// shared terminal library reads its handshake, its token claims and its configuration root from here.
builder.Services.AddSingleton(RuneChannelProfile.Build(builder.Configuration));

// MorganaAuthHandler self-issues short-lived JWTs with iss=rune on each request.
// The named HttpClient "Morgana" targets the Morgana base URL from configuration
// and runs through the handler so the Authorization header is set automatically.
builder.Services.AddTransient<MorganaAuthHandler>();
builder.Services.AddHttpClient("Morgana", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Rune:MorganaURL"]!);
}).AddHttpMessageHandler<MorganaAuthHandler>();

// ==============================================================================
// 5. SERVICES
// ==============================================================================
// Morgana.Terminal hosts what both TTY channels do the same way; the rest is Rune's own.
// MorganaClientService         : wraps start/send/end conversation lifecycle.
// MorganaStartRetryPolicy      : paces the attempts to open the conversation while Morgana is unreachable.
// ConversationLifecycleService : opens the conversation, waits for the presentation, runs the UI and ends it.
// WebhookReceiverService       : thin dispatcher invoked by the /morgana-hook endpoint.
// ConsoleUiService             : Spectre.Console Live(Layout) with sticky header + REPL body.
// LandingMessageService        : picks a random "warming up" line for the startup window.
// TerminalCellService          : rune-safe terminal-cell-width wrap, used by ConsoleUiService's own rendering.
builder.Services.AddSingleton<MorganaClientService>();
builder.Services.AddSingleton<MorganaStartRetryPolicy>();
builder.Services.AddSingleton<ConversationLifecycleService>();
builder.Services.AddSingleton<WebhookReceiverService>();
builder.Services.AddSingleton<ConsoleUiService>();
builder.Services.AddSingleton<LandingMessageService>();
builder.Services.AddSingleton<TerminalCellService>();

// ==============================================================================
// 5a. VIEWPORT RESIZE WATCHER - PLATFORM-SPECIFIC STRATEGY
// ==============================================================================
// ConsoleUiService keeps a fixed-viewport history slice anchored to the current terminal
// size. It needs a notification whenever the user resizes the host window so the
// slice can be recomputed without polling on every keystroke. Two strategies:
//   - POSIX (Linux + macOS): SIGWINCH delivered by the kernel — zero idle CPU.
//   - Windows: no SIGWINCH equivalent at this layer, fall back to a 250 ms
//     Console.Window* comparison loop.
// The decision is made once, here and the chosen implementation is injected
// into ConsoleUiService via DI so the UI itself stays platform-agnostic.
if (OperatingSystem.IsWindows())
    builder.Services.AddSingleton<IViewportResizeWatcher, PollingResizeWatcherService>();
else
    builder.Services.AddSingleton<IViewportResizeWatcher, SigWinchResizeWatcherService>();

WebApplication app = builder.Build();

// ==============================================================================
// 5b. SIGHUP HANDLER - GRACEFUL EXIT WHEN THE PARENT TTY DIES
// ==============================================================================
// .NET's WebApplication host already wires SIGTERM/SIGINT to ApplicationStopping
// on every platform. On POSIX, closing the host terminal window also sends SIGHUP
// to the foreground process group, but SIGHUP is *not* handled by default:
// without this registration, the docker CLI dies, the Rune container keeps
// running, --rm never fires and `compose down` then complains that
// morgana-network is still in use. Hooking SIGHUP to StopApplication makes the
// brutal-close case clean. Skipped on Windows because SIGHUP has no mapping
// there — the equivalent (CTRL_CLOSE_EVENT on the console X button) is already
// delivered as SIGTERM, which the host handles natively.
PosixSignalRegistration? sighupRegistration = OperatingSystem.IsWindows()
    ? null
    : PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
    {
        ctx.Cancel = true;
        app.Lifetime.StopApplication();
    });

// ==============================================================================
// 6. INBOUND WEBHOOK ENDPOINT
// ==============================================================================
// Morgana POSTs a serialized ChannelMessage here on every outbound turn (no JWT
// today — trust model is asymmetric by design, matching the WebhookChannelService
// convention). Bind the payload and hand it to the dispatcher. A delivery for another
// conversation is refused with 404, so Morgana logs the misdelivery on its side.
app.MapPost("/morgana-hook", async (HttpContext httpContext, WebhookReceiverService receiverService) =>
{
    ChannelMessage? message;
    try
    {
        message = await httpContext.Request.ReadFromJsonAsync<ChannelMessage>();
    }
    catch (JsonException)
    {
        return Results.BadRequest();
    }

    if (message is null)
        return Results.BadRequest();
    return receiverService.Dispatch(message) ? Results.Ok() : Results.NotFound();
});

// ==============================================================================
// 7. LIFECYCLE
// ==============================================================================
// Kestrel starts first so the listener is ready for Morgana's first delivery, then the
// conversation runs on this thread, which the live UI needs. Whatever way it ends, the
// SIGHUP hook is released and Kestrel stopped, so the process exits and docker's --rm
// reclaims the container.
await app.StartAsync();
try
{
    await app.Services.GetRequiredService<ConversationLifecycleService>().RunAsync(app.Lifetime.ApplicationStopping);
}
finally
{
    sighupRegistration?.Dispose();
    await app.StopAsync();
}