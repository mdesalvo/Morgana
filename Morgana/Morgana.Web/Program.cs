using System.Globalization;
using Akka.Actor;
using Akka.Actor.Setup;
using Akka.DependencyInjection;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Commands;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.AI.Telemetry;
using Morgana.Web.Extensions;
using Morgana.Web.Hubs;
using Morgana.Web.Services;

// ==============================================================================
// MORGANA - AI CONVERSATION FRAMEWORK
// ==============================================================================

// ==============================================================================
// SECTION 0: Culture
// ==============================================================================
// Invariant before anything else runs, so no thread this process starts inherits the host's locale:
// what Morgana writes to disk, hands to a model or sends to a channel reads the same on a workstation
// set to it-IT and in a container with no LANG at all. Morgana.AI states the culture explicitly
// wherever it formats a contract; this closes everything else, plugins included.

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ==============================================================================
// SECTION 1: ASP.NET Core Foundation
// ==============================================================================
// Standard ASP.NET Core services for web API and documentation

// The REST surface of the conversation and of the command catalogue: the controllers of this assembly.
builder.Services.AddControllers();

// Endpoint metadata for tooling that describes the REST surface.
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

// The one owner of a conversation's channel record: both the factory below and the concrete transports read it.
builder.Services.AddSingleton<IChannelMetadataStore, ChannelMetadataStore>();

// SignalR transport: the hub clients join and the service pushing to its groups, registered under "signalr".
builder.Services.AddSignalR();
builder.Services.AddSingleton<SignalRChannelService>();
builder.Services.AddSingleton<ChannelServiceRegistration>(sp =>
    new ChannelServiceRegistration(Constants.DeliveryModes.SignalR, sp.GetRequiredService<SignalRChannelService>()));

// Webhook transport: a named HTTP client so its handlers rotate and the service posting to the callback, registered under "webhook".
builder.Services.AddHttpClient(WebhookChannelService.HttpClientName);
builder.Services.AddSingleton<WebhookChannelService>();
builder.Services.AddSingleton<ChannelServiceRegistration>(sp =>
    new ChannelServiceRegistration(Constants.DeliveryModes.Webhook, sp.GetRequiredService<WebhookChannelService>()));

// Collects the registrations above: it answers which delivery modes this installation serves and hands a conversation its transport.
builder.Services.AddSingleton<IChannelServiceFactory, ChannelServiceFactory>();

// The decorator that degrades a rich message to the channel's capabilities before the transport sends it.
builder.Services.AddSingleton<AdaptingChannelService>(sp =>
    new AdaptingChannelService(
        sp.GetRequiredService<IChannelServiceFactory>(),
        sp.GetRequiredService<IChannelMetadataStore>(),
        sp.GetRequiredService<MorganaChannelAdapter>()));

// Producers ask for IChannelService and always get the adapting decorator, never a bare transport.
builder.Services.AddSingleton<IChannelService>(sp => sp.GetRequiredService<AdaptingChannelService>());

// ==============================================================================
// SECTION 3: CORS Configuration
// ==============================================================================
// Open CORS policy consistent with Morgana's channel-agnostic posture: the backend
// does not know its clients in advance, so the origin allowlist is replaced
// by per-request JWT validation as the real trust boundary. CORS here is the
// browser politeness layer; the bearer token is the security layer. In hardened
// deployments a reverse proxy / API gateway handles origin filtering upstream.

// The policy that Section 10 puts in front of every endpoint: any origin, header and method may knock.
builder.Services.AddCors(options =>
{
    options.AddPolicy("Channel", policy =>
    {
        // Credentials are allowed too, which a wildcard origin forbids: the origin is echoed back instead.
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

// Registered before any service that opens spans, so every turn is traced from the first message.
builder.Services.AddMorganaOpenTelemetry(builder.Configuration);

// ==============================================================================
// SECTION 4.2: Logging Infrastructure
// ==============================================================================
// Singleton logger for framework-level logging
// Actor loggers are created separately within each actor

// Controllers, filters and services ask for the plain ILogger: they all log under the one category named Morgana.
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

// Plugins are loaded before the container is built because the registry's startup checks scan the loaded assemblies.
// The container does not exist yet, so the loader logs through a bootstrap factory of its own.
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
// - IHostAddressService: Reports where this instance is reached — what Kestrel bound, or the public address a proxied deployment declares
// - IGuardRailService: Checks user messages for content safety and compliance
// - IClassifierService: Classifies user messages for proper agent activation
// - IPresenterService: Presents Morgana's capabilities at the first prompt
// - ICommandRegistryService: Publishes the commands channels may run on a conversation (every ICommand registered here)
// - ILLMService: Abstraction over LLM providers (Anthropic, Azure OpenAI, Ollama, OpenAI), three tiers Economy/Efficiency/Performance each served by its own provider (Morgana:LLM:Tiers)
// - ICommand: One per command a channel may run on a conversation

// Every registration below is a singleton: the services hold configuration and caches shared by all conversations.
builder.Services.AddSingleton<IMCPClientRegistryService, MCPClientRegistryService>();
builder.Services.AddSingleton<IToolRegistryService, ProvidesToolForIntentRegistryService>();
builder.Services.AddSingleton<IAgentConfigurationService, EmbeddedAgentConfigurationService>();
builder.Services.AddSingleton<IHostAddressService, KestrelHostAddressService>();
builder.Services.AddSingleton<IAgentDirectoryService, ConfigurationAgentDirectoryService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();
builder.Services.AddSingleton<IPromptComposerService, ConfigurationPromptComposerService>();
builder.Services.AddSingleton<IAgentRegistryService, HandlesIntentAgentRegistryService>();
builder.Services.AddSingleton<IGuardRailService, LLMGuardRailService>();
builder.Services.AddSingleton<IClassifierService, LLMClassifierService>();
builder.Services.AddSingleton<IPresenterService, LLMPresenterService>();
builder.Services.AddSingleton<ICommandRegistryService, CommandRegistryService>();
builder.Services.AddSingleton<ICommand, CompactHistoryCommand>();
// A factory because the service needs the configuration, the prompt resolver and the logger factory to build its tiers.
builder.Services.AddSingleton<ILLMService>(sp => {
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    IPromptResolverService promptResolver = sp.GetRequiredService<IPromptResolverService>();
    ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    // The tiers are built here, from the configuration, so a tier left unconfigured refuses the boot.
    ConfigurationLLMService llm = new ConfigurationLLMService(config, promptResolver, loggerFactory);

    // Wire dust accounting for the framework-actor path (CompleteWithSystemPromptAsync).
    // Done post-construction because the dust limiter depends on conversation persistence,
    // which is registered after this factory. Lazy resolution makes the order safe.
    llm.EnableDustAccounting(sp.GetRequiredService<IDustLimitService>());

    // The service goes out with dust accounting already wired.
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

// The options the persistence service binds, read from the section that holds the storage path and the encryption key.
builder.Services.Configure<Records.ConversationPersistenceOptions>(
    builder.Configuration.GetSection("Morgana:ConversationPersistence"));
// The record every other service reads or writes a conversation through.
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

// The windows per minute, hour and day with the authored refusal texts.
builder.Services.Configure<Records.RateLimitOptions>(
    builder.Configuration.GetSection("Morgana:RateLimiting"));

// The sliding-window limiter that ConversationLimitsFilter consults on every message and command.
builder.Services.AddSingleton<IRateLimitService, SQLiteRateLimitService>();

// ==============================================================================
// SECTION 7.3: Dust Limiting (token budget)
// ==============================================================================
// Orthogonal to rate limiting: caps token CONSUMPTION (not message frequency) over the
// conversation's lifetime. Shares the per-conversation SQLite database.
//
// - DustLimitingOptions: policy (budget + warning/error message templates)
// - Per-model pricing lives inline on each tier (Morgana:LLM:Tiers:{tier}:MagicDust)
//   and is resolved per-tier by ILLMService.GetPricing(tier) — no single process-wide pricing singleton.
//
// Configuration: Morgana:DustLimiting + Morgana:LLM:Tiers:{tier}:MagicDust in appsettings.json

// The lifetime budget with the warning and lockout texts.
builder.Services.Configure<Records.DustLimitingOptions>(
    builder.Configuration.GetSection("Morgana:DustLimiting"));

// The one owner of every dust question: the LLM service resolves it lazily, which is why it may be registered after it.
builder.Services.AddSingleton<IDustLimitService, SQLiteDustLimitService>();

// ==============================================================================
// SECTION 7.4: Authentication
// ==============================================================================
// Validates bearer tokens on incoming requests using a shared symmetric key (HMAC-SHA256).
// Fail-closed: unauthenticated requests are rejected with 401 when enabled.
// Extension point: swap IAuthenticationService in DI for API keys, mTLS, OAuth with external IdP.
//
// Configuration: Morgana:Authentication in appsettings.json

// The audience and the channel issuers with their keys.
builder.Services.Configure<Records.AuthenticationOptions>(
    builder.Configuration.GetSection("Morgana:Authentication"));

// The secret this installation's own agents consult each other under. Coined at every start and
// shared with nobody, so it is registered before the gate that proves it and the directory that
// signs with it.
builder.Services.AddSingleton<PeerRingKeyService>();

// The one token validator, shared by the channels' gate and the partners' gate.
builder.Services.AddSingleton<IAuthenticationService, JWTAuthenticationService>();

// ==============================================================================
// SECTION 8.1: Context Window Management
// ==============================================================================
// Service for reducing history messages sent to LLM (configurable summarization)

// The factory that agents ask for the reducer of their history.
builder.Services.AddSingleton<HistoryReducerService>();

// ==============================================================================
// SECTION 8.2: Adapters
// ==============================================================================
// - MorganaAgentAdapter: integrates Morgana agents with Microsoft.Extensions.AI abstractions.
// - MorganaChannelAdapter: transcodes rich outbound messages into a form that fits the
//                          target channel's capabilities (LLM-guided rewrite with a Markdig-based
//                          template fallback). Invoked implicitly by the AdaptingChannelService
//                          decorator registered in Section 2 — producers never call it directly.

// Builds every domain agent's chat pipeline.
builder.Services.AddSingleton<MorganaAgentAdapter>();

// Registered here because AdaptingChannelService in Section 2 resolves it on first use.
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

// One actor system for the process, wired to the container so that actors resolve their services from it.
builder.Services.AddSingleton(sp =>
{
    BootstrapSetup bootstrap = BootstrapSetup.Create();
    DependencyResolverSetup di = DependencyResolverSetup.Create(sp);
    ActorSystemSetup actorSystemSetup = bootstrap.And(di);
    return ActorSystem.Create(Constants.Morgana, actorSystemSetup);
});
// Terminates the actor system when the host stops.
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

// Every agent class the loaded assemblies declare, by intent: the plugins are loaded by now.
Dictionary<string, Type> discoveredAgents = HandlesIntentAgentRegistryService.DiscoverAgents();

// All discovered intents when peer consultation is on and none when it is off: publication is whole or nothing.
string[] publishedIntents = builder.Configuration.GetValue("Morgana:AgentToAgent:Enabled", true)
    ? [.. discoveredAgents.Keys]
    : [];

// Who may call these endpoints is declared, never assumed and the rule lives beside what it
// validates rather than here: one Morgana:AgentToAgent:Partners entry per partner, carrying its key
// beside what each direction of the relationship allows. Throws on the first incoherence, naming
// what to add. This installation's own agents need no declaration at all.
ConfigurationAgentDirectoryService.ValidateTrustConfiguration(builder.Configuration, publishedIntents);

// The one thing this installation says about itself and only where the binding cannot say it: behind
// an ingress or a published container port, what Kestrel binds is not where a peer knocks and a card
// naming the binding is refused by everyone who reads it. Undeclared, nothing is weighed here.
ConfigurationAgentDirectoryService.ValidatePublishedAddress(builder.Configuration, publishedIntents);

// One hosted agent and one A2A server per published intent. Its other half, MapMorganaA2AAsync, runs
// on the built application in section 10 — the container is sealed in between, so the pass cannot be
// one. What must not drift is the list and it does not: both halves are handed this same one.
builder.AddMorganaA2A(publishedIntents);

// ==============================================================================
// SECTION 10: Application Pipeline Configuration
// ==============================================================================
// Configures the HTTP request pipeline and middleware

WebApplication app = builder.Build();

// The domain, the agent registry and the command catalogue are read here rather than on first use. The
// first two refuse a name two plugins claim, a reserved name a plugin took, an intent with no agent or an
// agent with no intent; the catalogue refuses a command name claimed twice or a command declared wrongly.
// A refusal is only a startup refusal if something asks at startup. Left to first use, the same fault
// reaches a user as a conversation that never answers or a channel with no commands to offer.
await app.Services.GetRequiredService<IAgentConfigurationService>().GetIntentsAsync();
// The registry validates when it is first asked for an intent, never when it is merely resolved.
_ = app.Services.GetRequiredService<IAgentRegistryService>().GetAllIntents();
app.Services.GetRequiredService<ICommandRegistryService>();
// Builds the three tiers at startup, so that a tier left unconfigured refuses the boot rather than the first turn.
app.Services.GetRequiredService<ILLMService>();

// The middleware order is the request's path: CORS answers the browsers' preflight first, then HTTPS
// redirection and static files, then routing so that authorization sees the matched endpoint.
// The trust gate is the JWT filters on the controllers, not the origin.
app.UseCors("Channel");
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

// The REST surface of the conversation and of the commands.
app.MapControllers();

// The SignalR endpoint that channels join a conversation's group on.
app.MapHub<MorganaHub>("/morganaHub");

// The publication's second half: the endpoints, the cards and the address they learn once Kestrel
// has bound. Declared in section 9.5 and mapped here, because a route needs the built application.
await app.MapMorganaA2AAsync(publishedIntents);

// ==============================================================================
// SECTION 11: Application Startup
// ==============================================================================
// Starts the web application and actor system

// Blocks until the host stops: Kestrel binds here, which is when the A2A cards learn their address.
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