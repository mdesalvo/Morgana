using Akka.Actor;
using Akka.Actor.Setup;
using Akka.DependencyInjection;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.AI.Telemetry;
using Morgana.Web.Extensions;
using Morgana.Web.Hubs;
using Morgana.Web.Services;

// ==============================================================================
// MORGANA - AI CONVERSATION FRAMEWORK
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
// Outbound channel abstraction (IChannelService → AdaptingChannelService) that degrades rich messages
// via MorganaChannelAdapter, then routes to concrete transport (SignalR, Webhook, etc.) based on
// deliveryMode declared at conversation start. IChannelServiceFactory holds registrations; IsRegistered
// gates invalid deliveryModes at the start-conversation endpoint (400 rejection). Each concrete transport
// (SignalRChannelService for "signalr", WebhookChannelService for "webhook") is registered with its own
// ChannelServiceRegistration entry. Webhook uses IHttpClientFactory for handler rotation; does NOT sign
// POSTs (asymmetric trust model). Adding new channels requires registration here only; framework unchanged.

builder.Services.AddSingleton<IChannelMetadataStore, ChannelMetadataStore>();

//SignalR
builder.Services.AddSignalR();
builder.Services.AddSingleton<SignalRChannelService>();
builder.Services.AddSingleton<ChannelServiceRegistration>(sp =>
    new ChannelServiceRegistration(Constants.DeliveryModes.SignalR, sp.GetRequiredService<SignalRChannelService>()));
//WebHook
builder.Services.AddHttpClient(WebhookChannelService.HttpClientName);
builder.Services.AddSingleton<WebhookChannelService>();
builder.Services.AddSingleton<ChannelServiceRegistration>(sp =>
    new ChannelServiceRegistration(Constants.DeliveryModes.Webhook, sp.GetRequiredService<WebhookChannelService>()));

builder.Services.AddSingleton<IChannelServiceFactory, ChannelServiceFactory>();
builder.Services.AddSingleton<AdaptingChannelService>(sp =>
    new AdaptingChannelService(
        sp.GetRequiredService<IChannelServiceFactory>(),
        sp.GetRequiredService<IChannelMetadataStore>(),
        sp.GetRequiredService<MorganaChannelAdapter>()));
builder.Services.AddSingleton<IChannelService>(sp => sp.GetRequiredService<AdaptingChannelService>());

// ==============================================================================
// SECTION 3: CORS Configuration
// ==============================================================================
// Open CORS policy consistent with Morgana's channel-agnostic posture: the backend
// does not know its clients in advance, so the origin allowlist has been replaced
// with per-request JWT validation as the real trust boundary. CORS here is the
// browser politeness layer; the bearer token is the security layer. In hardened
// deployments a reverse proxy / API gateway handles origin filtering upstream.

builder.Services.AddCors(options =>
{
    options.AddPolicy("Channel", policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// ==============================================================================
// SECTION 4.1: OpenTelemetry
// ==============================================================================
// Distributed tracing per conversation with child spans for each turn (guard check,
// classifier intent+confidence, router agent selection, agent LLM execution with TTFT).
// Configured via appsettings.json → Morgana:OpenTelemetry (Enabled, ServiceName, Exporter: "otlp"/"console").

builder.Services.AddMorganaOpenTelemetry(builder.Configuration);

// ==============================================================================
// SECTION 4.2: Logging Infrastructure
// ==============================================================================
// Singleton logger for framework-level logging
// Actor loggers are created separately within each actor

builder.Services.AddSingleton<ILogger>(sp =>
    sp.GetRequiredService<ILoggerFactory>().CreateLogger(Constants.Morgana));

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
// - IPromptComposerService: Assembles what the model reads (composed prompt, tool descriptions, held-context declaration)
// - IAgentRegistryService: Maps intents to agent types for routing
// - IAgentDirectoryService: Describes agents to one another as A2A cards, for peer consultation
// - IHostAddressService: Reports the address this instance bound, so a card names a callable endpoint
// - IGuardRailService: Checks user messages for content safety and compliance
// - IClassifierService: Classifies user messages for proper agent activation
// - IPresenterService: Presents Morgana's capabilities at the first prompt
// - ILLMService: Abstraction over LLM providers (Anthropic, Azure OpenAI, OpenAI), two-tier Efficiency/Performance via each provider's Tiers{} configuration

builder.Services.AddSingleton<IMCPClientRegistryService, MCPClientRegistryService>();
builder.Services.AddSingleton<IToolRegistryService, ProvidesToolForIntentRegistryService>();
builder.Services.AddSingleton<IAgentConfigurationService, EmbeddedAgentConfigurationService>();
builder.Services.AddSingleton<IHostAddressService, KestrelHostAddressService>();
builder.Services.AddSingleton<IAgentDirectoryService, ConfigurationAgentDirectoryService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();
builder.Services.AddSingleton<IPromptComposerService, ConfigurationPromptComposerService>();
builder.Services.AddSingleton<ILLMTierValidationService, RequiresLLMTierValidationService>();
builder.Services.AddSingleton<IAgentRegistryService, HandlesIntentAgentRegistryService>();
builder.Services.AddSingleton<IGuardRailService, LLMGuardRailService>();
builder.Services.AddSingleton<IClassifierService, LLMClassifierService>();
builder.Services.AddSingleton<IPresenterService, LLMPresenterService>();
builder.Services.AddSingleton<ILLMService>(sp => {
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    IPromptResolverService promptResolver = sp.GetRequiredService<IPromptResolverService>();
    ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    string llmProvider = builder.Configuration["Morgana:LLM:Provider"]!;

    MorganaLLM llm = llmProvider.ToLowerInvariant() switch
    {
        "anthropic"   => new Morgana.AI.Abstractions.LLMs.Anthropic(config, promptResolver, loggerFactory),
        "azureopenai" => new Morgana.AI.Abstractions.LLMs.AzureOpenAI(config, promptResolver, loggerFactory),
        "ollama"      => new Morgana.AI.Abstractions.LLMs.Ollama(config, promptResolver, loggerFactory),
        "openai"      => new Morgana.AI.Abstractions.LLMs.OpenAI(config, promptResolver, loggerFactory),
        _ => throw new InvalidOperationException($"LLM Provider '{llmProvider}' not supported. Valid values: 'Anthropic', 'AzureOpenAI', 'Ollama', 'OpenAI'")
    };

    // Wire dust accounting for the framework-actor path (CompleteWithSystemPromptAsync).
    // Done post-construction because the dust limiter depends on conversation persistence,
    // which is registered after this factory. Lazy resolution makes the order safe.
    llm.EnableDustAccounting(sp.GetRequiredService<IDustLimitService>());

    return llm;
});

// ==============================================================================
// SECTION 7.1: Conversation Persistence
// ==============================================================================
// Encrypted file-based persistence for conversation state (AgentSession + Context)
// Enables resuming conversations across application restarts
//
// Storage Model: Each conversation stored as encrypted "morgana-{conversationId}.db" file
// Configuration: Morgana:ConversationPersistence in appsettings.json

// Plugin-owned stores outside DI (e.g. Examples' GreenhouseDatabaseHelper) read StoragePath
// from this OS environment variable rather than IConfiguration, because they have no access
// to the DI container. A value that only ever came from appsettings.json or User Secrets would
// never reach them otherwise, silently splitting "where conversations live" from "where the
// plugin's own database lives". Forward whatever Configuration resolved back onto the same
// variable so both read the identical, single-sourced value.
string? conversationStoragePath = builder.Configuration["Morgana:ConversationPersistence:StoragePath"];
if (!string.IsNullOrWhiteSpace(conversationStoragePath))
    Environment.SetEnvironmentVariable("Morgana__ConversationPersistence__StoragePath", conversationStoragePath);

builder.Services.Configure<Records.ConversationPersistenceOptions>(
    builder.Configuration.GetSection("Morgana:ConversationPersistence"));
builder.Services.AddSingleton<IConversationPersistenceService, SQLiteConversationPersistenceService>();

// ==============================================================================
// SECTION 7.2: Rate Limiting
// ==============================================================================
// Protects against spam, abuse and cost explosion by enforcing message quotas
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
// SECTION 7.3: Dust Limiting (token budget)
// ==============================================================================
// Orthogonal to rate limiting: caps token CONSUMPTION (not message frequency) over the
// conversation's lifetime. Shares the per-conversation SQLite database.
//
// - DustLimitingOptions: policy (budget + warning/error message templates)
// - Per-model pricing now lives inline on each Tiers{} entry (Morgana:LLM:{Provider}:Tiers{}.MagicDust)
//   and is resolved per-tier by ILLMService.GetPricing(tier) — no single process-wide pricing singleton.
//
// Configuration: Morgana:DustLimiting + Morgana:LLM:{Provider}:Tiers{}.MagicDust in appsettings.json

builder.Services.Configure<Records.DustLimitingOptions>(
    builder.Configuration.GetSection("Morgana:DustLimiting"));
builder.Services.AddSingleton<IDustLimitService, SQLiteDustLimitService>();

// ==============================================================================
// SECTION 7.4: Authentication
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

builder.Services.AddSingleton<HistoryReducerService>();

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
    return ActorSystem.Create(Constants.Morgana, actorSystemSetup);
});
builder.Services.AddHostedService<AkkaHostedService>();

// ==============================================================================
// SECTION 9.5: A2A Publication - agents of this installation, exposed to agents
// ==============================================================================
// Every agent of this installation is published as an A2A agent, so a colleague is reached through
// the protocol rather than through a private path. What is decided here is WHICH agents; how they
// are stood up is A2APublicationExtensions, in the two halves this section calls.
//
// The ring is raised whole or not at all: what an installation offers is what it can answer, never a
// side effect of which agents happen to consult one another here. Switched off, nothing below is
// stood up — no hosted agent, no server, no route, no card.

Dictionary<string, Type> discoveredAgents = HandlesIntentAgentRegistryService.DiscoverAgents();

string[] publishedIntents = builder.Configuration.GetValue("Morgana:AgentToAgent:Enabled", true)
    ? [.. discoveredAgents.Keys]
    : [];

// Who may call these endpoints is declared, never assumed and the rules live beside what they
// validate rather than here: identity in Morgana:Authentication:Issuers, where every entry carries
// the role its key was cut for and reach in Morgana:AgentToAgent:InboundSystems. Throws on the
// first incoherence, naming what to add.
ConfigurationAgentDirectoryService.ValidateTrustConfiguration(
    builder.Configuration,
    publishedIntents,
    HandlesIntentAgentRegistryService.DeclaresLocalConsultations(discoveredAgents));

// One hosted agent and one A2A server per published intent. Its other half, MapMorganaA2AAsync, runs
// on the built application in section 10 — the container is sealed in between, so the pass cannot be
// one. What must not drift is the list and it does not: both halves are handed this same one.
builder.AddMorganaA2A(publishedIntents);

// ==============================================================================
// SECTION 10: Application Pipeline Configuration
// ==============================================================================
// Configures the HTTP request pipeline and middleware

WebApplication app = builder.Build();

app.UseCors("Channel");                 // Open CORS; trust gate is JWT, not origin
app.UseHttpsRedirection();              // Redirect HTTP to HTTPS
app.UseStaticFiles();                   // Serve static files (if any)
app.UseRouting();                       // Enable endpoint routing
app.UseAuthorization();                 // Enable authorization middleware
app.MapControllers();                   // Map REST API controllers
app.MapHub<MorganaHub>("/morganaHub");  // Map SignalR hub endpoint

// The publication's second half: the endpoints, the cards and the address they learn once Kestrel
// has bound. Declared in section 9.5 and mapped here, because a route needs the built application.
await app.MapMorganaA2AAsync(publishedIntents);

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

// ==============================================================================
// TEST ENTRY POINT VISIBILITY
// ==============================================================================
// Top-level statements compile into an implicitly-generated internal Program class.
// Declaring it partial and public lets the prompt harness (PromptHarness) reach this
// assembly's entry point and boot the real host in-process on an ephemeral Kestrel port.
// It has no effect on production behaviour.

public partial class Program;