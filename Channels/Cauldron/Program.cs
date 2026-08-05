using Cauldron.Handlers;
using Cauldron.Interfaces;
using Cauldron.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ==============================================================================
// CAULDRON - MORGANA'S HOME
// ==============================================================================
// This is the main entry point for the Cauldron application.
// Cauldron is a modular web layer that talks with the Morgana conversational AI platform.

// ============================================================================
// 1. BLAZOR SERVER CONFIGURATION
// ============================================================================
// Blazor Server provides server-side rendering with real-time UI updates via SignalR.
// The UI state lives on the server, and DOM updates are sent to the client via WebSocket.

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
// separate chat state, and everything here dies with the connection that owns it.
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
app.UseStaticFiles();                   // Serve static files (CSS, JS, images)
app.UseRouting();                       // Enable endpoint routing

// Blazor Server endpoints
app.MapBlazorHub();                     // SignalR hub carrying Blazor's own UI updates
app.MapFallbackToPage("/_Host");        // Every unmatched route renders the single page (SPA behavior)

// ============================================================================
// 6. APPLICATION STARTUP
// ============================================================================
// Start the application and listen for requests.

await app.RunAsync();