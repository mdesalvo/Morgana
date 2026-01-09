using Cauldron.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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

// ============================================================================
// 3. CUSTOM SERVICES
// ============================================================================
// Register Cauldron-specific services for SignalR client and message handling.

// SignalR client service for real-time communication with Morgana backend
// Manages WebSocket connection, automatic reconnection, and message routing
builder.Services.AddScoped<MorganaSignalRService>();

// Dynamic configuration-based landing message service
// Selects a random welcome message during the "magic spakle" loading
builder.Services.AddSingleton<MorganaLandingMessageService>();

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

/*
 * ============================================================================
 * CAULDRON APPLICATION ARCHITECTURE
 * ============================================================================
 * 
 * Cauldron is a Blazor Server application providing a real-time chat interface
 * for interacting with the Morgana AI conversational system.
 * 
 * TECHNOLOGY STACK:
 * ├── Blazor Server (Server-side rendering with SignalR UI updates)
 * ├── ASP.NET Core 10.0 (Web framework)
 * ├── SignalR Client (Real-time communication with Morgana backend)
 * └── HttpClient (REST API calls to Morgana)
 * 
 * COMMUNICATION PATTERNS:
 * 
 * 1. REST API (Cauldron → Morgana)
 *    - POST /api/conversation/start → Start new conversation
 *    - POST /api/conversation/{id}/message → Send user message
 *    - HTTP 202 Accepted (async processing pattern)
 * 
 * 2. SignalR (Morgana → Cauldron)
 *    - WebSocket connection to /conversationHub
 *    - ReceiveMessage events with agent responses
 *    - Group-based routing (one group per conversation)
 * 
 * 3. Blazor SignalR (Cauldron Server → Browser)
 *    - Automatic UI updates via Blazor Server SignalR
 *    - DOM diffing and patch application
 *    - Component state management
 * 
 * APPLICATION FLOW:
 * 
 * 1. User opens browser → Loads _Host.cshtml
 * 2. Blazor Server initializes → Establishes SignalR connection for UI
 * 3. Index.razor OnInitializedAsync → MorganaSignalRService.StartAsync()
 * 4. SignalR client connects to Morgana backend
 * 5. POST /api/conversation/start → Creates conversation
 * 6. Join SignalR conversation group
 * 7. Receive presentation message via SignalR
 * 8. User types message → POST to Morgana
 * 9. Morgana processes → Sends response via SignalR
 * 10. UI updates automatically via Blazor Server
 * 
 * CONFIGURATION (appsettings.json):
 * {
 *   "Morgana": {
 *     "BaseUrl": "https://localhost:5001",
 *     "LandingMessage": "Summoning Morgana...",
 *     "AgentExitMessage": "{0} has completed the spell."
 *   }
 * }
 */