using Akka.Actor;
using Akka.Actor.Setup;
using Akka.DependencyInjection;
using Microsoft.Extensions.AI;
using Morgana.AgentsFramework.Adapters;
using Morgana.AgentsFramework.Interfaces;
using Morgana.AgentsFramework.Services;
using Morgana.Foundations.Interfaces;
using Morgana.Startup.Hubs;
using Morgana.Startup.Services;

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
// SECTION 2: SignalR Configuration
// ==============================================================================
// Real-time communication infrastructure for bi-directional client-server messaging
// - ConversationHub: SignalR hub for conversation group management
// - ISignalRBridgeService: Actor-to-SignalR bridge for sending messages to clients

builder.Services.AddSignalR();
builder.Services.AddSingleton<ISignalRBridgeService, SignalRBridgeService>();

// ==============================================================================
// SECTION 3: CORS Configuration
// ==============================================================================
// Cross-Origin Resource Sharing for Blazor client application (Cauldron)
// Allows the Blazor frontend (Cauldron) to communicate with the Morgana backend

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazor", policy =>
    {
        policy.WithOrigins(builder.Configuration["Morgana:Cauldron:BaseUrl"]!) // Cauldron Blazor app URL
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// ==============================================================================
// SECTION 4: Logging Infrastructure
// ==============================================================================
// Singleton logger for framework-level logging (not actor-specific)
// Actor loggers are created separately within each actor

builder.Services.AddSingleton<ILogger>(sp =>
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Morgana"));

// ==============================================================================
// SECTION 5: Plugin System - Dynamic Agent Loading
// ==============================================================================
// Loads external assemblies containing custom Morgana agents at startup
// Configuration: appsettings.json -> Morgana:Plugins:Assemblies
// 
// This enables domain-specific agents to be developed separately and loaded
// without modifying the core Morgana framework.
//
// Example plugin: Morgana.ExampleAgents (contains BillingAgent, ContractAgent, etc.)

using (ILoggerFactory bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddConsole()))
{
    PluginLoaderService pluginLoaderService = new PluginLoaderService(
        builder.Configuration,
        bootstrapLoggerFactory.CreateLogger<PluginLoaderService>());
    pluginLoaderService.LoadPluginAssemblies();
}

// ==============================================================================
// SECTION 6: Morgana.AI Services - Core Framework
// ==============================================================================
// These services provide the core Morgana.AI framework functionality:
//
// - IMCPClientRegistryService: Handles discovery of configured MCP servers
// - IToolRegistryService: Discovers and registers tools provided by agents
// - IAgentConfigurationService: Loads agent and intent configurations
// - IPromptResolverService: Resolves prompt templates from configuration
// - IAgentRegistryService: Maps intents to agent types for routing
// - ILLMService: Abstraction over LLM providers (Anthropic, Azure OpenAI)
// - IChatClient: Microsoft.Extensions.AI chat client for LLM interactions

builder.Services.AddSingleton<IMCPClientRegistryService, MCPClientRegistryService>();
builder.Services.AddSingleton<IToolRegistryService, ProvidesToolForIntentRegistryService>();
builder.Services.AddSingleton<IAgentConfigurationService, EmbeddedAgentConfigurationService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();
builder.Services.AddSingleton<IAgentRegistryService, HandlesIntentAgentRegistryService>();

// LLM Service Configuration
// Supports multiple LLM providers via configuration (Morgana:LLM:Provider)
// Valid values: "anthropic", "azureopenai"
builder.Services.AddSingleton<ILLMService>(sp => {
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    IPromptResolverService promptResolver = sp.GetRequiredService<IPromptResolverService>();
    string llmProvider = builder.Configuration["Morgana:LLM:Provider"]!;

    return llmProvider.ToLowerInvariant() switch
    {
        "anthropic" => new AnthropicService(config, promptResolver),
        "azureopenai" => new AzureOpenAIService(config, promptResolver),
        _ => throw new InvalidOperationException($"LLM Provider '{llmProvider}' not supported. Valid values: 'AzureOpenAI', 'Anthropic'")
    };
});
builder.Services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<ILLMService>().GetChatClient());

// ==============================================================================
// SECTION 7: Agent Adapter
// ==============================================================================
// Adapter for integrating Morgana agents with Microsoft.Extensions.AI abstractions

builder.Services.AddSingleton<MorganaAgentAdapter>();

// ==============================================================================
// SECTION 8: Akka.NET Actor System
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
// SECTION 9: Application Pipeline Configuration
// ==============================================================================
// Configures the HTTP request pipeline and middleware

WebApplication app = builder.Build();

app.UseCors("AllowBlazor");                      // Enable CORS for Blazor client
app.UseHttpsRedirection();                       // Redirect HTTP to HTTPS
app.UseStaticFiles();                            // Serve static files (if any)
app.UseRouting();                                // Enable endpoint routing
app.UseAuthorization();                          // Enable authorization middleware
app.MapControllers();                            // Map REST API controllers
app.MapHub<ConversationHub>("/conversationHub"); // Map SignalR hub endpoint

// ==============================================================================
// SECTION 10: Application Startup
// ==============================================================================
// Starts the web application and actor system

await app.RunAsync();

// ==============================================================================
// APPLICATION FLOW SUMMARY
// ==============================================================================
//
// 1. CLIENT CONNECTS
//    - Establishes SignalR connection to /conversationHub
//    - Calls JoinConversation(conversationId) hub method
//
// 2. CLIENT STARTS CONVERSATION
//    - POST /api/conversation/start { conversationId: "..." }
//    - Creates ConversationManagerActor and ConversationSupervisorActor
//    - Supervisor automatically generates and sends presentation via SignalR
//
// 3. CLIENT SENDS MESSAGE
//    - POST /api/conversation/{id}/message { text: "..." }
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
//    - POST /api/conversation/{id}/end
//    - Stops ConversationManagerActor and all child actors
//    - Client calls LeaveConversation(conversationId) and disconnects SignalR
//
// ==============================================================================