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

builder.Services.AddRazorPages();       // Enable Razor Pages (used for _Host.cshtml)
builder.Services.AddServerSideBlazor(); // Enable Blazor Server with SignalR for UI updates

// ============================================================================
// 2. HTTP CLIENT CONFIGURATION
// ============================================================================
// Configure HttpClient for making REST API calls to the Morgana backend.
// The base address is loaded from appsettings.json (Morgana:BaseUrl).

// Global HttpClient factory registration (for flexibility)
builder.Services.AddHttpClient();

// Scoped HttpClient with configured base address for Morgana API calls
// Used by Index.razor to POST conversation start and messages
builder.Services.AddScoped(sp => new HttpClient
{
    BaseAddress = new Uri(builder.Configuration["Morgana:BaseUrl"]!)  // e.g., "https://localhost:5001"
});

// ==============================================================================
// SECTION 4: Logging Infrastructure
// ==============================================================================
builder.Services.AddSingleton<ILogger>(sp =>
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Cauldron"));

// ============================================================================
// 3. CUSTOM SERVICES
// ============================================================================
// Register Cauldron-specific services for SignalR client and message handling.

// SignalR client service for real-time communication with Morgana backend
// Manages WebSocket connection, automatic reconnection, and message routing
builder.Services.AddScoped<MorganaSignalRService>();

// Dynamic configuration-based landing message service
// Selects a random welcome message during the "magic sparkle" loading
builder.Services.AddSingleton<MorganaLandingMessageService>();

// Conversation persistence service using ProtectedLocalStorage
// Stores conversation ID in browser localStorage with automatic AES-256 encryption
// Enables seamless conversation resume across browser sessions
builder.Services.AddScoped<IConversationStorageService, ProtectedLocalStorageService>();
builder.Services.AddScoped<IConversationHistoryService, MorganaConversationHistoryService>();

// ============================================================================
// 4. APPLICATION PIPELINE
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
app.MapBlazorHub();                     // SignalR hub for Blazor Server UI updates
app.MapFallbackToPage("/_Host");        // Fallback to _Host.cshtml for all unmatched routes (SPA behavior)

// ============================================================================
// 5. APPLICATION STARTUP
// ============================================================================
// Start the application and listen for requests.

await app.RunAsync();