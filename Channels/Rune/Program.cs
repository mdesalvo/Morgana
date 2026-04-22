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
// 1. LOGGING - KEEP THE TUI CLEAN
// ==============================================================================
// Spectre.Console renders over the terminal via Live, so any stray log line from
// Kestrel/ASP.NET Core corrupts the layout. Drop all providers; errors still
// surface through explicit UI messages (see ConsoleUi's red system line).
builder.Logging.ClearProviders();

// ==============================================================================
// 2. OUTBOUND TO MORGANA - JWT + HTTP CLIENT
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
// 3. DOMAIN SERVICES
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
// 4. INBOUND WEBHOOK ENDPOINT
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
// 5. LIFECYCLE
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
