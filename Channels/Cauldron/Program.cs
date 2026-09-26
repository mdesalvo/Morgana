using System.Globalization;
using Cauldron.Handlers;
using Cauldron.Interfaces;
using Cauldron.Services;
using Microsoft.AspNetCore.SignalR.Client;

// Invariant before anything else runs, so no thread or circuit inherits the host's locale: the page
// reads the same on a workstation set to it-IT and in a container with no LANG at all.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ==============================================================================
// CAULDRON - MORGANA'S HOME
// ==============================================================================
// This is the main entry point for the Cauldron application.
// Cauldron is a modular web layer that talks with the Morgana conversational AI platform.

// ============================================================================
// 0. CONFIGURATION GATE - THE TWO SETTINGS CAULDRON CANNOT RUN WITHOUT
// ============================================================================
// Backend address and signing key are each fatal on their own: without them
// Cauldron serves a chat that can neither reach Morgana nor be trusted by it.
// Refused here, both problems named at once, rather than one failed request at
// a time once visitors are already on the page. There is no callback address to
// check: replies are pushed down the hub connection Cauldron itself opens.

// An address Cauldron can actually dial, for the REST calls and for the hub alike: anything
// else (a bare host:port, a path, a scheme nobody serves) is parsed happily and only fails
// once the first call is already on its way.
static bool IsReachableUrl(string? value) =>
    Uri.TryCreate(value, UriKind.Absolute, out Uri? url)
    && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);

string?[] requiredSettings =
[
    IsReachableUrl(builder.Configuration["Cauldron:MorganaURL"])
        ? null
        : "Cauldron:MorganaURL must be Morgana's absolute http(s) base URL (for example https://localhost:5001).",
    // The shipped value is a marker, not a key: left in place it buys a 401 from Morgana at the
    // first conversation, where the reason is far less obvious than it is here
    builder.Configuration["Cauldron:Authentication:SymmetricKey"] is not { Length: > 0 } key || key == "_SECURE_OVERRIDE_"
        ? "Cauldron:Authentication:SymmetricKey is required and must match Morgana's Authentication:Issuers entry for Name=cauldron. Supply it through user-secrets or an environment variable."
        : null
];
if (requiredSettings.Any(problem => problem is not null))
{
    Console.Error.WriteLine("Cauldron cannot start with the configuration it was given:");
    foreach (string problem in requiredSettings.OfType<string>())
        Console.Error.WriteLine($"  - {problem}");
    return;
}

// ============================================================================
// 1. BLAZOR SERVER CONFIGURATION
// ============================================================================
// Blazor Server provides server-side rendering with real-time UI updates via SignalR.
// The UI state lives on the server and DOM updates are sent to the client via WebSocket.

builder.Services.AddRazorPages();       // Razor Pages, used only to serve _Host.cshtml
builder.Services.AddServerSideBlazor(); // Blazor Server: UI state lives here, DOM diffs go over SignalR

// ============================================================================
// 2. HTTP CLIENT CONFIGURATION
// ============================================================================
// Configure HttpClient for making REST API calls to the Morgana backend.
// The base address is loaded from appsettings.json (Cauldron:MorganaURL).

// Authentication handler for Morgana API calls — self-issues JWT tokens
// signed with the shared symmetric key (same key configured in Morgana.Web)
builder.Services.AddTransient<MorganaAuthHandler>();

// Named HttpClient with configured base address and automatic Bearer token injection
// Used by Index.razor and ConversationHistoryService for Morgana API calls
builder.Services.AddHttpClient("Morgana", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Cauldron:MorganaURL"]!); // Morgana (Backend)
}).AddHttpMessageHandler<MorganaAuthHandler>();

// Default scoped HttpClient resolved from the named "Morgana" registration
// Lets services take a plain HttpClient dependency and still get the authenticated one
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("Morgana"));

// ==============================================================================
// 3. LOGGING INFRASTRUCTURE
// ==============================================================================
builder.Services.AddSingleton<ILogger>(sp =>
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Cauldron"));

// ============================================================================
// 4. CUSTOM SERVICES
// ============================================================================
// Register Cauldron-specific services for SignalR client and message handling.

// SignalR client service for real-time communication with Morgana backend
// Manages WebSocket connection, automatic reconnection and message routing
builder.Services.AddScoped<SignalRService>();

// Pace of the attempts to reach Morgana's hub, at first connection and after a drop
// Singleton: it holds no state, every circuit reads the same schedule
builder.Services.AddSingleton<IRetryPolicy, MorganaHubRetryPolicy>();

// Dynamic configuration-based landing message service
// Selects a random welcome message during the "magic sparkle" loading
// Singleton: the message pool is configuration, identical for every visitor
builder.Services.AddSingleton<ILandingMessageService, LandingMessageService>();

// Converts message and rich-card Markdown to HTML, sanitizing away injected script/markup
builder.Services.AddSingleton<IMarkdownRendererService, MarkdownRendererService>();

// Conversation persistence & history services using ProtectedLocalStorage
// Stores conversation ID in browser localStorage with automatic AES-256 encryption
// Enables seamless conversation resume across browser sessions
builder.Services.AddScoped<IConversationStorageService, ProtectedLocalStorageService>();
builder.Services.AddScoped<IConversationHistoryService, ConversationHistoryService>();

// Chat state, conversation lifecycle and streaming services
// All scoped, which in Blazor Server means one instance per circuit: two browser tabs get
// separate chat state and everything here dies with the connection that owns it.
builder.Services.AddScoped<IChatStateService, ChatStateService>();
builder.Services.AddScoped<IConversationLifecycleService, ConversationLifecycleService>();
builder.Services.AddScoped<IStreamingService, StreamingService>();

// ============================================================================
// 5. APPLICATION PIPELINE
// ============================================================================
// Build the application and configure the HTTP request processing pipeline.

WebApplication app = builder.Build();

// Production-only middleware
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");  // Global exception handler page
    app.UseHsts();                      // HTTP Strict Transport Security
}

// Request pipeline configuration
app.UseHttpsRedirection();              // Redirect HTTP → HTTPS

// ============================================================================
// 6. EMBEDDING CONTROL (frame-ancestors)
// ============================================================================
// Widget puts this app in an <iframe> on third-party sites and nothing in
// ASP.NET Core constrains framing by default — which for a chat surface is the wrong
// default in both directions: unconfigured, any site could frame it and phish through it.
//
// Closed unless configured: with no origins listed, only Cauldron's own pages may frame it
// ('self', which is what makes /widget/morgana.html work out of the box). Each origin an
// operator adds to Cauldron:Widget:AllowedEmbedOrigins is a site allowed to host the widget.
string[] allowedEmbedOrigins = app.Configuration
    .GetSection("Cauldron:Widget:AllowedEmbedOrigins")
    .Get<string[]>() ?? [];
string frameAncestors = allowedEmbedOrigins.Length > 0
    ? $"frame-ancestors 'self' {string.Join(' ', allowedEmbedOrigins)}"
    : "frame-ancestors 'self'";
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = frameAncestors;
    await next();
});

app.UseStaticFiles();                   // Serve static files (CSS, JS, images)
app.UseRouting();                       // Enable endpoint routing

// Blazor Server endpoints
app.MapBlazorHub();                     // SignalR hub carrying Blazor's own UI updates
app.MapFallbackToPage("/_Host");        // Every unmatched route renders the single page (SPA behavior)

// Health check endpoint for monitoring (status + uptime)
DateTimeOffset startedAt = DateTimeOffset.UtcNow;
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    uptime = DateTimeOffset.UtcNow - startedAt
}));

// ============================================================================
// 7. APPLICATION STARTUP
// ============================================================================
// Start the application and listen for requests.

await app.RunAsync();