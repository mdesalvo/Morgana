using System.Reflection;
using A2A;
using A2A.AspNetCore;
using Akka.Actor;
using Akka.Actor.Setup;
using Akka.DependencyInjection;
using Microsoft.Agents.AI.Hosting;
using Microsoft.Agents.AI.Hosting.A2A;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.AI.SessionStores;
using Morgana.AI.Telemetry;
using Morgana.Web.Hubs;
using Morgana.Web.Services;

// ==============================================================================
// MORGANA - AI CONVERSATION FRAMEWORK
// ==============================================================================
// Actor-based AI conversation framework routing requests to specialized agents by intent.
// Stack: ASP.NET Core REST + SignalR real-time + Akka.NET orchestration + plugin-based agents.
// LLM providers: Anthropic, Azure OpenAI, OpenAI, Ollama. Pipeline: Guard → Classifier → Router → Agents.
// ==============================================================================

// Microsoft marks the A2A hosting run-mode API experimental (MEAI001). It is the documented way to
// declare that an agent answers inline rather than as a background task, which is exactly what a
// consultation is, so it is used deliberately — as MorganaAgentAdapter already does for IChatReducer.
#pragma warning disable MEAI001

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

builder.Services.AddSignalR();
builder.Services.AddSingleton<SignalRChannelService>();
builder.Services.AddSingleton<ChannelServiceRegistration>(sp =>
    new ChannelServiceRegistration("signalr", sp.GetRequiredService<SignalRChannelService>()));

builder.Services.AddHttpClient(WebhookChannelService.HttpClientName);
builder.Services.AddSingleton<WebhookChannelService>();
builder.Services.AddSingleton<ChannelServiceRegistration>(sp =>
    new ChannelServiceRegistration("webhook", sp.GetRequiredService<WebhookChannelService>()));

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

    Morgana.AI.Abstractions.MorganaLLM llm = llmProvider.ToLowerInvariant() switch
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
    return ActorSystem.Create("Morgana", actorSystemSetup);
});
builder.Services.AddHostedService<AkkaHostedService>();

// ==============================================================================
// SECTION 9.5: A2A Publication - agents of this installation, exposed to agents
// ==============================================================================
// An agent named by somebody's [ConsultsAgent] is published as an A2A agent, so that colleague is
// reached through the protocol rather than through a private path. The chain is entirely Microsoft's:
// AddA2AServer wraps the agent in an AIHostAgent and an A2AAgentHandler, MapA2AJsonRpc exposes it,
// and MapWellKnownAgentCard makes it discoverable. Morgana contributes exactly two pieces: the agent
// carrying a request to the conversation's actor (MorganaHostedAgent) and the session store turning
// the A2A context id into that conversation's identity (MorganaHostedAgentSessionStore).
//
// NOTHING below is stood up when no agent declares a colleague, or when the feature is switched off:
// no hosted agent, no server, no route, no card. A deployment whose agents do not consult one another
// is the deployment it was before any of this existed, and pays for A2A neither in surface nor in
// startup work. Only the intents actually named are published, so an agent nobody consults exposes
// no endpoint either — publish everything discovered instead, and it becomes one line here.

Dictionary<string, Type> discoveredAgents = HandlesIntentAgentRegistryService.DiscoverAgents();

string[] publishedIntents = builder.Configuration.GetValue("Morgana:AgentToAgent:Enabled", true)
    ?
    [
        .. discoveredAgents.Values
            .SelectMany(agentType => agentType.GetCustomAttributes<ConsultsAgentAttribute>())
            .Select(consultsAgent => consultsAgent.Intent)
            .Where(discoveredAgents.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ]
    : [];

// An installation that publishes A2A endpoints must be able to CALL them: every outbound
// consultation is signed under the "morgana" issuer, and without that key a colleague resolves to
// nothing and simply never appears in the asking agent's tool list — a topology that validated
// cleanly at startup, failing silently on the first conversation. That is the same defect the
// [ConsultsAgent] checks exist to prevent, so it is refused in the same place and at the same time
// rather than logged as a warning nobody reads. Fail fast, and say which of the two ways out to take.
if (publishedIntents.Length > 0
    && ConfigurationAgentDirectoryService.ResolvePeerSigningKey(builder.Configuration) is null)
{
    throw new InvalidOperationException(
        $"Peer consultation is enabled and {publishedIntents.Length} agent(s) are published over A2A "
        + $"({string.Join(", ", publishedIntents)}), but no usable signing key is configured for the "
        + $"'{Constants.AgentToAgent.IssuerName}' issuer: declare it under Morgana:Authentication:Issuers "
        + "with a real SymmetricKey (User Secrets or environment), or set Morgana:AgentToAgent:Enabled "
        + "to false to run without peer consultation.");
}

TimeSpan a2aRequestTimeout = TimeSpan.FromSeconds(
    builder.Configuration.GetValue("Morgana:ActorSystem:TimeoutSeconds", 180));

foreach (string publishedIntent in publishedIntents)
{
    builder.Services
        .AddAIAgent(publishedIntent, (serviceProvider, agentName) => new MorganaHostedAgent(
            agentName,
            serviceProvider.GetRequiredService<IAgentDirectoryService>()
                .GetAgentCardAsync(agentName).GetAwaiter().GetResult()?.Description ?? agentName,
            serviceProvider.GetRequiredService<IAgentRegistryService>(),
            serviceProvider.GetRequiredService<IPromptComposerService>(),

            // The actor system, deferred rather than resolved here: it is built around the very
            // dependency resolver this provider is, and standing one up to hand to an agent nobody
            // has asked a question of yet would invert that order.
            serviceProvider.GetRequiredService<ActorSystem>,
            a2aRequestTimeout,
            serviceProvider.GetRequiredService<ILogger>()))

        // The session store is where a request's A2A context id becomes a Morgana conversation.
        // Not isolation-key scoped: the context id IS the partition here, and Morgana's own
        // per-conversation database already isolates everything the conversation owns.
        .WithSessionStore(
            (serviceProvider, agentName) => new MorganaHostedAgentSessionStore(serviceProvider.GetRequiredService<ILogger>()),
            ServiceLifetime.Singleton,
            false)

        // Runs the agent inline and answers with a Message rather than a Task: a consultation is a
        // single question answered in full, which is what the published card advertises.
        .AddA2AServer(options => options.AgentRunMode = AgentRunMode.DisallowBackground);
}

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

// One JSON-RPC endpoint and one well-known agent card per published agent. The card is what makes
// the agent discoverable: A2ACardResolver reads it to learn which interface to bind a client to, and
// Morgana's own directory resolves a colleague through that same fetch rather than short-circuiting
// to an in-process object.
if (publishedIntents.Length > 0)
{
    IAgentDirectoryService agentDirectory = app.Services.GetRequiredService<IAgentDirectoryService>();
    IAuthenticationService a2aAuthentication = app.Services.GetRequiredService<IAuthenticationService>();

    foreach (string publishedIntent in publishedIntents)
    {
        string agentPath = $"{Constants.AgentToAgent.AgentPathPrefix}/{publishedIntent}";

        AgentCard? publishedCard = await agentDirectory.GetAgentCardAsync(publishedIntent);
        if (publishedCard is null)
            continue;

        app.MapA2AJsonRpc(publishedIntent, agentPath)
           .AddEndpointFilter(async (invocationContext, next) =>
                await AuthenticateA2ARequestAsync(invocationContext, next, a2aAuthentication));

        // The card itself stays open: discovery is what tells a caller how to authenticate, and a
        // card behind authentication cannot be found by anyone not already knowing how to reach it.
        app.MapWellKnownAgentCard(publishedCard, agentPath);
    }

    // The cards above were projected while the endpoints were still being mapped, before Kestrel had
    // bound anything, so none of them names an address yet. The moment it has, the directory fills
    // them in — the well-known endpoint serialises its card on every request, so a card read after
    // this point carries the interface, and nothing ever had to be told the application's own URL.
    app.Lifetime.ApplicationStarted.Register(() => agentDirectory.PublishInterfacesAsync().GetAwaiter().GetResult());
}

// The A2A endpoints are mapped outside the controllers, so they carry no gate of their own. This
// applies the very gate MorganaController applies: the same issuer whitelist, the same audience,
// fail-closed. An agent consulting a colleague presents a token issued under the "morgana" issuer.
static async ValueTask<object?> AuthenticateA2ARequestAsync(
    EndpointFilterInvocationContext invocationContext,
    EndpointFilterDelegate next,
    IAuthenticationService authenticationService)
{
    string? authorization = invocationContext.HttpContext.Request.Headers.Authorization.FirstOrDefault();

    if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        return Results.Unauthorized();

    Records.AuthenticationResult authentication = await authenticationService.AuthenticateAsync(authorization["Bearer ".Length..]);

    return authentication.IsAuthenticated ? await next(invocationContext) : Results.Unauthorized();
}

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