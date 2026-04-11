using Akka.Actor;
using Akka.Actor.Setup;
using Akka.DependencyInjection;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.AI.Telemetry;
using Morgana.Web.Hubs;
using Morgana.Web.Services;

// ==============================================================================
// MORGANA - AI CONVERSATION FRAMEWORK
// ==============================================================================
// This is the main entry point for the Morgana application.
// Morgana is an actor-based AI conversation framework that routes user requests
// to specialized agents based on intent classification.
//
// Architecture Overview:
// - ASP.NET Core Web API for REST endpoints
// - SignalR for real-time bi-directional communication
// - Akka.NET actor system for conversation orchestration
// - Plugin-based extensibility for custom agents
// - LLM abstraction supporting multiple providers (Anthropic, Azure OpenAI)
//
// Key Components:
// 1. Controllers: REST API for conversation lifecycle management
// 2. Hubs: SignalR real-time messaging
// 3. Actors: Conversation orchestration pipeline (Guard → Classifier → Router → Agents)
// 4. Services: Infrastructure services (SignalR bridge, LLM, configuration)
// 5. Plugins: Dynamically loaded domain-specific agents
// ==============================================================================

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ==============================================================================
// SECTION 1: ASP.NET Core Foundation
// ==============================================================================
// Standard ASP.NET Core services for web API and documentation

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ==============================================================================
// SECTION 2: Outbound Channel
// ==============================================================================
// Wires the outbound channel through which actors deliver messages to the end user.
// IChannelService is the abstraction; concrete implementations carry the transport.
//
// - IChannelService: resolved as AdaptingChannelService, a decorator that routes every
//                    outbound ChannelMessage through MorganaChannelAdapter before handing
//                    it to the wrapped concrete channel. Producers keep calling
//                    channelService.SendMessageAsync(...) unchanged; degradation (rich
//                    cards → prose, quick replies → inline list, markdown strip, length
//                    truncation) is applied uniformly and inherited for free by any
//                    future channel implementation.
// - SignalRChannelService: currently the only concrete IChannelService implementation,
//                          backing the Cauldron web UI with full expressive capabilities.
//                          AddSignalR() is its transport-level dependency; when additional
//                          channels are introduced, their own transport-level dependencies
//                          (e.g. HTTP client factories, message bus clients) belong here.

builder.Services.AddSignalR();
builder.Services.AddSingleton<SignalRChannelService>();
builder.Services.AddSingleton<IChannelService>(sp =>
    new AdaptingChannelService(
        sp.GetRequiredService<SignalRChannelService>(),
        sp.GetRequiredService<MorganaChannelAdapter>()));

// ==============================================================================
// SECTION 3: CORS Configuration
// ==============================================================================
// Cross-Origin Resource Sharing for Blazor client application (Cauldron)
// Allows the Blazor frontend (Cauldron) to communicate with the Morgana backend

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazor", policy =>
    {
        policy.WithOrigins(builder.Configuration["Morgana:CauldronURL"]!) // Cauldron (Frontend)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// ==============================================================================
// SECTION 4.1: OpenTelemetry
// ==============================================================================
// Distributed tracing for conversation-level observability.
// Produces a trace per conversation with child spans for each turn, guard check,
// intent classification, agent routing, and agent execution.
//
// Trace structure:
//   morgana.conversation     ← lifetime of a conversation
//     morgana.turn           ← one per user message
//       morgana.guard        ← content moderation result
//       morgana.classifier   ← intent + confidence
//       morgana.router       ← selected agent path
//       morgana.agent        ← LLM execution, TTFT, response preview
//
// Configuration: appsettings.json → Morgana:OpenTelemetry
//   Enabled:       true/false
//   ServiceName:   "Morgana"
//   Exporter:      "otlp" | "console"

builder.Services.AddMorganaOpenTelemetry(builder.Configuration);

// ==============================================================================
// SECTION 4.2: Logging Infrastructure
// ==============================================================================
// Singleton logger for framework-level logging
// Actor loggers are created separately within each actor

builder.Services.AddSingleton<ILogger>(sp =>
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Morgana"));

// ==============================================================================
// SECTION 5: Plugin System - Dynamic Agent Loading
// ==============================================================================
// Loads external assemblies containing custom Morgana agents at startup
// Configuration: appsettings.json -> Morgana:Plugins:Directories
// 
// This enables domain-specific agents to be developed separately and loaded
// without modifying the core Morgana framework.

using (ILoggerFactory bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddConsole()))
{
    PluginLoaderService pluginLoaderService = new PluginLoaderService(
        builder.Configuration,
        bootstrapLoggerFactory.CreateLogger<PluginLoaderService>());
    pluginLoaderService.LoadPluginAssemblies();
}

// ==============================================================================
// SECTION 6: Morgana.Agents Services - Core Framework
// ==============================================================================
// These services provide the core Morgana.Agents framework functionality:
//
// - IMCPClientRegistryService: Handles discovery of configured MCP servers
// - IToolRegistryService: Discovers and registers tools provided by agents
// - IAgentConfigurationService: Loads agent and intent configurations
// - IPromptResolverService: Resolves prompt templates from configuration
// - IAgentRegistryService: Maps intents to agent types for routing
// - IGuardRailService: Checks user messages for content safety and compliance
// - IClassifierService: Classifies user messages for proper agent activation
// - IPresenterService: Presents Morgana's capabilities at the first prompt
// - ILLMService: Abstraction over LLM providers (Anthropic, Azure OpenAI, OpenAI)
// - IChatClient: Microsoft.Extensions.AI chat client for LLM interactions

builder.Services.AddSingleton<IMCPClientRegistryService, MCPClientRegistryService>();
builder.Services.AddSingleton<IToolRegistryService, ProvidesToolForIntentRegistryService>();
builder.Services.AddSingleton<IAgentConfigurationService, EmbeddedAgentConfigurationService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();
builder.Services.AddSingleton<IAgentRegistryService, HandlesIntentAgentRegistryService>();
builder.Services.AddSingleton<IGuardRailService, LLMGuardRailService>();
builder.Services.AddSingleton<IClassifierService, LLMClassifierService>();
builder.Services.AddSingleton<IPresenterService, LLMPresenterService>();
builder.Services.AddSingleton<ILLMService>(sp => {
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    IPromptResolverService promptResolver = sp.GetRequiredService<IPromptResolverService>();
    string llmProvider = builder.Configuration["Morgana:LLM:Provider"]!;

    return llmProvider.ToLowerInvariant() switch
    {
        "anthropic"   => new Morgana.AI.Abstractions.Anthropic(config, promptResolver),
        "azureopenai" => new Morgana.AI.Abstractions.AzureOpenAI(config, promptResolver),
        "ollama"      => new Morgana.AI.Abstractions.Ollama(config, promptResolver),
        "openai"      => new Morgana.AI.Abstractions.OpenAI(config, promptResolver),
        _ => throw new InvalidOperationException($"LLM Provider '{llmProvider}' not supported. Valid values: 'Anthropic', 'AzureOpenAI', 'Ollama', 'OpenAI'")
    };
});
builder.Services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<ILLMService>().GetChatClient());

// ==============================================================================
// SECTION 7.1: Conversation Persistence
// ==============================================================================
// Encrypted file-based persistence for conversation state (AgentSession + Context)
// Enables resuming conversations across application restarts
//
// Storage Model: Each conversation stored as encrypted "morgana-{conversationId}.db" file
// Configuration: Morgana:ConversationPersistence in appsettings.json

builder.Services.Configure<Records.ConversationPersistenceOptions>(
    builder.Configuration.GetSection("Morgana:ConversationPersistence"));
builder.Services.AddSingleton<IConversationPersistenceService, SQLiteConversationPersistenceService>();


// ==============================================================================
// SECTION 7.2: Rate Limiting
// ==============================================================================
// Protects against spam, abuse, and cost explosion by enforcing message quotas
// Stores request logs in the same SQLite database as conversation persistence
//
// Architecture:
// - SQLiteRateLimitService depends on IConversationPersistenceService
// - Delegates database initialization to persistence service (single source of truth)
//
// Configuration: Morgana:RateLimiting in appsettings.json
// Storage: Reuses conversation SQLite databases (morgana-{conversationId}.db)

builder.Services.Configure<Records.RateLimitOptions>(
    builder.Configuration.GetSection("Morgana:RateLimiting"));
builder.Services.AddSingleton<IRateLimitService, SQLiteRateLimitService>();

// ==============================================================================
// SECTION 7.3: Authentication
// ==============================================================================
// Validates bearer tokens on incoming requests using a shared symmetric key (HMAC-SHA256).
// Fail-closed: unauthenticated requests are rejected with 401 when enabled.
// Extension point: swap IAuthenticationService in DI for API keys, mTLS, OAuth with external IdP.
//
// Configuration: Morgana:Authentication in appsettings.json

builder.Services.Configure<Records.AuthenticationOptions>(
    builder.Configuration.GetSection("Morgana:Authentication"));
builder.Services.AddSingleton<IAuthenticationService, JWTAuthenticationService>();

// ==============================================================================
// SECTION 8.1: Context Window Management
// ==============================================================================
// Service for reducing history messages sent to LLM (configurable summarization)

builder.Services.AddSingleton<SummarizingChatReducerService>();

// ==============================================================================
// SECTION 8.2: Adapters
// ==============================================================================
// - MorganaAgentAdapter: integrates Morgana agents with Microsoft.Extensions.AI abstractions.
// - MorganaChannelAdapter: transcodes rich outbound messages into a form that fits the
//                          target channel's capabilities (LLM-guided rewrite with a Markdig-based
//                          template fallback). Invoked implicitly by the AdaptingChannelService
//                          decorator registered in Section 2 — producers never call it directly.

builder.Services.AddSingleton<MorganaAgentAdapter>();
builder.Services.AddSingleton<MorganaChannelAdapter>();

// ==============================================================================
// SECTION 9: Akka.NET Actor System
// ==============================================================================
// Creates and configures the Akka.NET actor system for conversation orchestration
//
// Architecture:
// - BootstrapSetup: Basic actor system configuration
// - DependencyResolverSetup: Integrates with ASP.NET Core DI for actor dependencies
// - ActorSystemSetup: Combined setup passed to ActorSystem.Create
//
// Actor Hierarchy:
//   ConversationManagerActor (per conversation)
//     └── ConversationSupervisorActor (orchestrates FSM)
//           ├── GuardActor (content moderation)
//           ├── ClassifierActor (intent classification)
//           ├── RouterActor (routes to specialized agents)
//           └── Specialized Agents (BillingAgent, ContractAgent, etc.)
//
// Lifecycle: Managed by AkkaHostedService (graceful shutdown on app stop)

builder.Services.AddSingleton(sp =>
{
    BootstrapSetup bootstrap = BootstrapSetup.Create();
    DependencyResolverSetup di = DependencyResolverSetup.Create(sp);
    ActorSystemSetup actorSystemSetup = bootstrap.And(di);
    return ActorSystem.Create("Morgana", actorSystemSetup);
});
builder.Services.AddHostedService<AkkaHostedService>();

// ==============================================================================
// SECTION 10: Application Pipeline Configuration
// ==============================================================================
// Configures the HTTP request pipeline and middleware

WebApplication app = builder.Build();

app.UseCors("AllowBlazor");             // Enable CORS for Blazor client
app.UseHttpsRedirection();              // Redirect HTTP to HTTPS
app.UseStaticFiles();                   // Serve static files (if any)
app.UseRouting();                       // Enable endpoint routing
app.UseAuthorization();                 // Enable authorization middleware
app.MapControllers();                   // Map REST API controllers
app.MapHub<MorganaHub>("/morganaHub");  // Map SignalR hub endpoint

// ==============================================================================
// SECTION 11: Application Startup
// ==============================================================================
// Starts the web application and actor system

await app.RunAsync();

// ==============================================================================
// APPLICATION FLOW SUMMARY
// ==============================================================================
//
// 1. CLIENT CONNECTS
//    - Establishes SignalR connection to /morganaHub
//    - Calls JoinConversation(conversationId) hub method
//
// 2. CLIENT STARTS CONVERSATION
//    - POST /api/morgana/conversation/start { conversationId: "..." }
//    - Creates ConversationManagerActor and ConversationSupervisorActor
//    - Supervisor automatically generates and sends presentation via SignalR
//
// 3. CLIENT SENDS MESSAGE
//    - POST /api/morgana/conversation/{id}/message { text: "..." }
//    - Message flows through actor pipeline:
//      GuardActor → ClassifierActor → RouterActor → SpecializedAgent
//    - Response sent to client via SignalR (ReceiveMessage event)
//
// 4. MULTI-TURN CONVERSATIONS
//    - If agent returns IsCompleted=false, supervisor remembers active agent
//    - Subsequent messages route directly to active agent (skip classification)
//    - Agent signals IsCompleted=true when done, conversation returns to idle
//
// 5. CLIENT ENDS CONVERSATION
//    - POST /api/morgana/conversation/{id}/end
//    - Stops ConversationManagerActor and all child actors
//    - Client calls LeaveConversation(conversationId) and disconnects SignalR
//
// ==============================================================================