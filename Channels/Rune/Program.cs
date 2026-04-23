using System.Text;
using Rune.Handlers;
using Rune.Messages.Contracts;
using Rune.Services;
using Spectre.Console;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ==============================================================================
// RUNE - POOR-BUT-HONEST WEBHOOK CHANNEL FOR MORGANA
// ==============================================================================
// Rune is a minimal CLI reference channel talking to Morgana over the webhook
// delivery mode. It declares a tight capability budget (200-char hard limit, no
// rich cards, no quick replies, no streaming, no markdown) so Morgana's channel
// adapter rewrite path is exercised on every turn.
//
// Architecture: a Kestrel-hosted HTTPS listener on port 5003 receives inbound
// ChannelMessage payloads at POST /morgana-hook and drives a Spectre.Console
// terminal UI; user input is captured from stdin and sent back to Morgana via
// the REST conversation endpoints, authenticated with a self-issued JWT signed
// under iss=rune (the matching entry must live in Morgana's Authentication:Issuers).

// ==============================================================================
// 0. CONSOLE ENCODING - UTF-8 I/O
// ==============================================================================
// On Windows the default OutputEncoding is the OEM/ANSI code page (CP437/CP850/CP1252),
// none of which covers the BMP glyphs Rune renders: the header arrow (→ U+2192),
// the input prompt chevron (› U+203A), the conversation-id ellipsis (… U+2026), and
// the courtesy dashes. Under those code pages the console falls back to the "symbol
// for delete" glyph (␦), which looks like a tofu box to the user. Forcing both streams
// to UTF-8 up front is idempotent on Linux/macOS (already UTF-8) and makes Spectre's
// Unicode output render correctly on Windows without asking the user to chcp 65001.
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// ==============================================================================
// 1. TTY GATE - FAIL CLOSED ON NON-INTERACTIVE TERMINALS
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
// 2. LOGGING - KEEP THE TUI CLEAN
// ==============================================================================
// Spectre.Console renders over the terminal via Live, so any stray log line from
// Kestrel/ASP.NET Core corrupts the layout. Drop all providers; errors still
// surface through explicit UI messages (see ConsoleUi's red system line).
builder.Logging.ClearProviders();

// ==============================================================================
// 3. OUTBOUND TO MORGANA - JWT + HTTP CLIENT
// ==============================================================================
// MorganaAuthHandler self-issues short-lived JWTs with iss=rune on each request.
// The named HttpClient "Morgana" targets the Morgana base URL from configuration
// and runs through the handler so the Authorization header is set automatically.
builder.Services.AddTransient<MorganaAuthHandler>();
builder.Services.AddHttpClient("Morgana", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Rune:MorganaURL"]!);
}).AddHttpMessageHandler<MorganaAuthHandler>();

// ==============================================================================
// 4. DOMAIN SERVICES
// ==============================================================================
// MorganaClient     : wraps start/send/end conversation lifecycle.
// WebhookReceiver   : thin dispatcher invoked by the /morgana-hook endpoint.
// ConsoleUi         : Spectre.Console Live(Layout) with sticky header + REPL body.
// All singletons: the process hosts exactly one Rune "session" at a time.
builder.Services.AddSingleton<MorganaClient>();
builder.Services.AddSingleton<WebhookReceiver>();
builder.Services.AddSingleton<ConsoleUi>();

WebApplication app = builder.Build();

// ==============================================================================
// 5. INBOUND WEBHOOK ENDPOINT
// ==============================================================================
// Morgana POSTs a serialized ChannelMessage here on every outbound turn (no JWT
// today — trust model is asymmetric by design, matching the WebhookChannelService
// convention). Bind the payload and hand it to the dispatcher.
app.MapPost("/morgana-hook", async (HttpContext httpContext, WebhookReceiver receiver) =>
{
    ChannelMessage? message = await httpContext.Request.ReadFromJsonAsync<ChannelMessage>();
    if (message is null)
        return Results.BadRequest();
    receiver.Dispatch(message);
    return Results.Ok();
});

// ==============================================================================
// 6. LIFECYCLE
// ==============================================================================
// Start Kestrel asynchronously (the UI must be on the main thread), wire the
// webhook → UI callback, open a conversation with Morgana, hand over to the
// ConsoleUi loop, and on exit gracefully end the conversation and stop Kestrel.
await app.StartAsync();

MorganaClient morganaClient = app.Services.GetRequiredService<MorganaClient>();
WebhookReceiver webhook = app.Services.GetRequiredService<WebhookReceiver>();
ConsoleUi ui = app.Services.GetRequiredService<ConsoleUi>();

webhook.OnMessage = ui.EnqueueIncoming;

string conversationId;
try
{
    conversationId = await morganaClient.StartConversationAsync();
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Failed to open conversation with Morgana:[/] {Markup.Escape(ex.Message)}");
    await app.StopAsync();
    return;
}

try
{
    await ui.RunAsync(
        conversationId,
        text => morganaClient.SendMessageAsync(conversationId, text));
}
finally
{
    await morganaClient.EndConversationAsync(conversationId);
    await app.StopAsync();
}
