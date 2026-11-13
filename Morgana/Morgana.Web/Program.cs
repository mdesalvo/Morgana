using System.Globalization;
using System.Reflection;
using Akka.Actor;
using Akka.Actor.Setup;
using Akka.DependencyInjection;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Adapters;
using Morgana.AI.Attributes;
using Morgana.AI.Commands;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.Web.Extensions;
using Morgana.Web.Hubs;
using Morgana.Web.Services;

// ==============================================================================
// MORGANA - AI CONVERSATION FRAMEWORK
// ==============================================================================
// The whole composition root, top to bottom: the order of the sections is the boot order and part of
// what makes it work (plugins before the registry's checks, the actor system after DI, the cards
// projected before Kestrel binds).

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

// The REST surface of the conversation and of the commands, gated by the MVC filters each action declares.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// ==============================================================================
// SECTION 2: Outbound Channel
// ==============================================================================
// Every producer writes to IChannelService, which is AdaptingChannelService: it fits the message to the
// channel through MorganaChannelAdapter, then hands it to the transport registered under the deliveryMode
// the channel announced at the handshake. A new transport is one more registration here and nothing else.

// The one owner of each conversation's handshake, which the transports and the adapter both read.
builder.Services.AddSingleton<IChannelMetadataStore, ChannelMetadataStore>();

// SignalR: pushes to the group a channel joins before it starts the conversation.
builder.Services.AddSignalR();
builder.Services.AddSingleton<SignalRChannelService>();
builder.Services.AddSingleton<ChannelServiceRegistration>(sp =>
    new ChannelServiceRegistration(Constants.DeliveryModes.SignalR, sp.GetRequiredService<SignalRChannelService>()));

// Webhook: POSTs to the callback the channel declared, on a named client so its pipeline is configured apart from any other.
builder.Services.AddHttpClient(WebhookChannelService.HttpClientName);
builder.Services.AddSingleton<WebhookChannelService>();
builder.Services.AddSingleton<ChannelServiceRegistration>(sp =>
    new ChannelServiceRegistration(Constants.DeliveryModes.Webhook, sp.GetRequiredService<WebhookChannelService>()));

// The factory holds every registration: the start endpoint asks it whether a deliveryMode is served before accepting a handshake.
builder.Services.AddSingleton<IChannelServiceFactory, ChannelServiceFactory>();

// The decorator every producer reaches as IChannelService, so no message leaves without being fitted to its channel.
builder.Services.AddSingleton<AdaptingChannelService>(sp =>
    new AdaptingChannelService(
        sp.GetRequiredService<IChannelServiceFactory>(),
        sp.GetRequiredService<IChannelMetadataStore>(),
        sp.GetRequiredService<MorganaChannelAdapter>(),
        sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<IChannelService>(sp => sp.GetRequiredService<AdaptingChannelService>());

// ==============================================================================
// SECTION 3: CORS Configuration
// ==============================================================================
// Morgana does not know its channels in advance, so no origin is refused: the trust boundary is the
// bearer token every call carries, validated per issuer. A hardened deployment filters origins at the
// reverse proxy or gateway in front of it.

builder.Services.AddCors(options =>
{
    // Any origin, credentials included, since a browser channel such as Cauldron sends its token with every call.
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

// One trace per turn, its guard, classifier, router and agent spans beneath it, sent to the exporters
// that Morgana:OpenTelemetry enables.
builder.Services.AddMorganaOpenTelemetry(builder.Configuration);

// ==============================================================================
// SECTION 4.2: Logging Infrastructure
// ==============================================================================

// The logger the framework's services take as a plain ILogger, its lines filed under Morgana; actors log through Akka.
builder.Services.AddSingleton<ILogger>(sp =>
    sp.GetRequiredService<ILoggerFactory>().CreateLogger(Constants.Morgana));

// ==============================================================================
// SECTION 5: Plugin System
// ==============================================================================
// Domains and LLM providers arrive as assemblies in the plugin directories (Morgana:Plugins:Directories,
// "plugins" always first), so neither needs a change to this host. They are loaded before anything is
// registered that discovers types, since every registry scans what the process has loaded.

// The container is not built yet, so the loader logs through a console factory of its own, disposed with the scan.
using (ILoggerFactory bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddConsole()))
{
    PluginLoaderService pluginLoaderService = new PluginLoaderService(
        builder.Configuration,
        bootstrapLoggerFactory.CreateLogger<PluginLoaderService>());

    // The domains' assemblies join the process, where the agent, tool and configuration registries find them.
    pluginLoaderService.LoadAgents();

    // The providers come from the plugin directories alone: the host knows only those it finds there.
    // The configuration then picks among them per tier.
    foreach (Type languageModelType in pluginLoaderService.LoadLanguageModels())
    {
        // Each provider joins the container under the common base, so the LLM service receives them all and indexes them by name.
        builder.Services.AddSingleton(typeof(MorganaLanguageModel), languageModelType);
    }
}

// ==============================================================================
// SECTION 6: Core Framework Services
// ==============================================================================
// Every extension point is an interface of Morgana.AI with its default here, replaceable by swapping the line:
// - IMCPClientRegistryService: the pooled connections to the MCP servers agents declare
// - IToolRegistryService: the tool class of each intent
// - IAgentConfigurationService: the intents and prompts merged from every plugin's agents.json
// - IHostAddressService: where this installation is reached, what Kestrel bound or the declared public address
// - IAgentDirectoryService: the agents described to one another as A2A cards
// - IPromptResolverService / IPromptComposerService: the prompts by id and everything a model reads
// - IAgentRegistryService: the agent type of each intent
// - IGuardRailService: the guards on users' messages, tool results, offers and partners' questions
// - IClassifierService / IPresenterService: the intent of a message and Morgana's welcome
// - ICommandRegistryService and each ICommand: the commands channels may run on a conversation
// - ILanguageModelService: the three tiers, each on the provider it names

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
builder.Services.AddSingleton<ILanguageModelService>(sp => {
    // What the tiers are built from: the configuration, the providers the plugins brought and the framework prompts.
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    IPromptResolverService promptResolver = sp.GetRequiredService<IPromptResolverService>();
    ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    // The tiers are built here, from the configuration, so a tier left unconfigured refuses the boot.
    ConfigurationLanguageModelService llm = new ConfigurationLanguageModelService(
        config, sp.GetServices<MorganaLanguageModel>(), promptResolver, loggerFactory);

    // The framework actors' calls are charged to the conversation. It is wired after construction because
    // Alembic and the harness build the same service with no dust ledger at all.
    llm.EnableDustAccounting(sp.GetRequiredService<IDustLimitService>());

    // The one ILanguageModelService of the host, its tiers built and the framework's calls metered.
    return llm;
});

// ==============================================================================
// SECTION 7.1: Conversation Persistence
// ==============================================================================
// One encrypted SQLite database per conversation, morgana-{conversationId}.sqlite under
// Morgana:ConversationPersistence:StoragePath: it holds every participant's session, the handshake,
// the shared context and the limiters' ledgers, so a conversation survives a restart.

// A plugin's own store outside DI (the example plugin's ExamplesDatabaseHelper) reads the storage path from
// this environment variable. A value given only in appsettings.json or User Secrets would never reach it,
// splitting where conversations live from where the plugin's database lives, so it is written back here.
string? conversationStoragePath = builder.Configuration["Morgana:ConversationPersistence:StoragePath"];
if (!string.IsNullOrWhiteSpace(conversationStoragePath))
    Environment.SetEnvironmentVariable("Morgana__ConversationPersistence__StoragePath", conversationStoragePath);

// The folder and the AES key; the service refuses to start without either.
builder.Services.Configure<Records.ConversationPersistenceOptions>(
    builder.Configuration.GetSection("Morgana:ConversationPersistence"));

// The rate and dust limiters keep their tables in the same database files and open them through this one instance.
builder.Services.AddSingleton<SQLiteConversationPersistenceService>();

// The record every other service reads or writes a conversation through.
builder.Services.AddSingleton<IConversationPersistenceService>(serviceProvider => serviceProvider.GetRequiredService<SQLiteConversationPersistenceService>());

// ==============================================================================
// SECTION 7.2: Rate Limiting
// ==============================================================================
// Sliding windows per minute, hour and day on every call that starts work on a conversation, logged in
// the conversation's own database. Fails open: a limiter that cannot count never stops a user.

// The three caps and the refusal each window answers with.
builder.Services.Configure<Records.RateLimitOptions>(
    builder.Configuration.GetSection("Morgana:RateLimiting"));
builder.Services.AddSingleton<IRateLimitService, SQLiteRateLimitService>();

// ==============================================================================
// SECTION 7.3: Dust Limiting (token budget)
// ==============================================================================
// Beside the rate limit, which caps how often a user calls, the dust budget caps how many tokens a
// conversation consumes over its lifetime. A model's price is declared by its provider plugin and read
// per tier through ILanguageModelService.GetMorganaDust.

// The budget and the notices the user reads as it runs out.
builder.Services.Configure<Records.DustLimitingOptions>(
    builder.Configuration.GetSection("Morgana:DustLimiting"));
builder.Services.AddSingleton<IDustLimitService, SQLiteDustLimitService>();

// ==============================================================================
// SECTION 7.4: Authentication
// ==============================================================================
// Every call but the health probe carries a bearer token signed with the key of the issuer it names:
// one key per channel and per partner, so a leak opens one door. Fail-closed: an unproven token is a 401.
// Another scheme (API keys, mTLS, an external identity provider) replaces IAuthenticationService here.

// The audience and the channels admitted, each with its key.
builder.Services.Configure<Records.AuthenticationOptions>(
    builder.Configuration.GetSection("Morgana:Authentication"));

// The secret this installation's own agents consult each other under. Coined at every start and
// shared with nobody, so it is registered before the gate that proves it and the directory that
// signs with it.
builder.Services.AddSingleton<PeerRingKeyService>();

// The one gate that proves a token, read by the channels' filter and the partners' alike.
builder.Services.AddSingleton<IAuthenticationService, JWTAuthenticationService>();

// ==============================================================================
// SECTION 8.1: Context Window Management
// ==============================================================================

// Builds the reducer that folds an agent's older history into a summary; none when Morgana:HistoryReducer is off.
builder.Services.AddSingleton<HistoryReducerService>();

// ==============================================================================
// SECTION 8.2: Adapters
// ==============================================================================

// Builds each agent from its prompt, tools, MCP servers and colleagues; every plugin agent takes it in its constructor.
builder.Services.AddSingleton<MorganaAgentAdapter>();

// Fits a rich message to a limited channel, reached only through the AdaptingChannelService of section 2.
builder.Services.AddSingleton<MorganaChannelAdapter>();

// ==============================================================================
// SECTION 9: Akka.NET Actor System
// ==============================================================================
// Per conversation: ConversationManagerActor, its ConversationSupervisorActor and beneath it the guard,
// the classifier, the router and the domain agents the router raises.

builder.Services.AddSingleton(sp =>
{
    // Actors are built by the same container as every service, so an agent's constructor takes its dependencies like any class.
    BootstrapSetup bootstrap = BootstrapSetup.Create();
    DependencyResolverSetup di = DependencyResolverSetup.Create(sp);
    ActorSystemSetup actorSystemSetup = bootstrap.And(di);
    return ActorSystem.Create(Constants.Morgana, actorSystemSetup);
});

// Terminates the actor system when the host stops, so no conversation's actors outlive the process's shutdown.
builder.Services.AddHostedService<AkkaHostedService>();

// ==============================================================================
// SECTION 9.5: A2A Publication - agents of this installation, exposed to agents
// ==============================================================================
// Every agent of this installation is published as an A2A agent, so a colleague is reached through
// the protocol rather than through a private path. What is decided here is which agents; how they
// are stood up is A2APublicationExtensions, in the two halves this section calls.
//
// The ring is raised whole or not at all: what an installation offers is what it can answer, never a
// side effect of which agents happen to consult one another here. Switched off, nothing below is
// stood up: no hosted agent, no server, no route, no card.

// Every agent the loaded plugins carry, by intent; the container is not built, so the registry is read statically.
Dictionary<string, Type> discoveredAgents = HandlesIntentAgentRegistryService.DiscoverAgents();

// All of them or none, as the ring is raised whole.
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
// on the built application in section 10, the container being sealed in between. What must not drift
// is the list and it does not: both halves are handed this same one.
builder.AddMorganaA2A(publishedIntents);

// ==============================================================================
// SECTION 10: Application Pipeline Configuration
// ==============================================================================

WebApplication app = builder.Build();

// The domain, the agent registry and the command catalogue are read here rather than on first use. The
// first two refuse a name two plugins claim, a reserved name a plugin took, an intent with no agent or an
// agent with no intent; the catalogue refuses a command name claimed twice or a command declared wrongly.
// A refusal is only a startup refusal if something asks at startup. Left to first use, the same fault
// reaches a user as a conversation that never answers or a channel with no commands to offer.
await app.Services.GetRequiredService<IAgentConfigurationService>().GetIntentsAsync();

// The registry validates when it is first asked for an intent, never when it is merely resolved.
_ = app.Services.GetRequiredService<IAgentRegistryService>().GetAllIntents();

// The catalogue validates on construction, so resolving it is the whole check.
app.Services.GetRequiredService<ICommandRegistryService>();

// Builds the three tiers at startup, so that a tier left unconfigured refuses the boot rather than the first turn.
app.Services.GetRequiredService<ILanguageModelService>();

// Morgana examines what every declared MCP server offers before the first conversation, as an antivirus does ahead
// of use. A server unreachable now is examined at its first use with the same code and the admission never throws,
// so a server cannot stop the boot.
if (builder.Configuration.GetValue("Morgana:ActorSystem:EnableToolGuardrail", true))
{
    // The guard that keeps the go/no-go of every external source.
    IGuardRailService startupGuardRailService = app.Services.GetRequiredService<IGuardRailService>();

    // The registry that knows every agent type by intent.
    IAgentRegistryService startupAgentRegistryService = app.Services.GetRequiredService<IAgentRegistryService>();

    // Every server any agent declares, once each: the pool key is internal to Morgana.AI so the transport and the
    // command, which it is made of, identify a server here.
    List<UsesMCPServerAttribute> declaredMCPServers =
    [
        .. startupAgentRegistryService.GetAllIntents()
            .Select(startupAgentRegistryService.ResolveAgentFromIntent)
            .OfType<Type>()
            .SelectMany(agentType => agentType.GetCustomAttributes<UsesMCPServerAttribute>())
            .DistinctBy(attribute => (attribute.Transport, attribute.Command))
    ];

    // Every server is examined at once: the boot waits for the slowest rather than for the sum.
    await Task.WhenAll(declaredMCPServers.Select(startupGuardRailService.AdmitMCPServerToolsAsync));
}

// The open policy of section 3, ahead of routing so a browser's preflight is answered before any gate.
app.UseCors("Channel");

// A channel that calls over plain HTTP is sent to HTTPS, where its bearer token cannot be read on the wire.
app.UseHttpsRedirection();

// Serves wwwroot, which holds Morgana's avatar for the channels that draw it.
app.UseStaticFiles();

// Routing before authorization, so an endpoint's own metadata, such as the health probe's anonymity, is known to it.
app.UseRouting();
app.UseAuthorization();

// The conversation and command controllers, each gated by its MVC filters.
app.MapControllers();

// The hub SignalR channels join a conversation's group through.
app.MapHub<MorganaHub>("/morganaHub");

// The publication's second half: the endpoints, the cards and the address they learn once Kestrel
// has bound. Declared in section 9.5 and mapped here, because a route needs the built application.
await app.MapMorganaA2AAsync(publishedIntents);

// ==============================================================================
// SECTION 11: Application Startup
// ==============================================================================

// Kestrel binds and serves until the host is stopped, which also terminates the actor system.
await app.RunAsync();

// ==============================================================================
// APPLICATION FLOW SUMMARY
// ==============================================================================
//
// 1. CLIENT CONNECTS
//    - A SignalR channel connects to /morganaHub and calls JoinConversation(conversationId);
//      a webhook channel declares its callbackUrl instead
//
// 2. CLIENT STARTS CONVERSATION
//    - POST /api/morgana/conversation/start { conversationId, channelMetadata }
//    - The handshake is settled on record, then the manager and its supervisor are created
//    - The supervisor sends Morgana's welcome over the channel
//
// 3. CLIENT SENDS MESSAGE
//    - POST /api/morgana/conversation/{id}/message { text }
//    - The message flows through the pipeline: guard, classifier, router, agent
//    - The answer reaches the channel through its transport (ReceiveMessage over SignalR, a POST over webhook)
//
// 4. MULTI-TURN CONVERSATIONS
//    - An agent stays in service by default and later messages go straight to it, skipping classification
//    - It hands the conversation back by calling SetTurnEnding and the conversation returns to Morgana
//
// 5. CLIENT ENDS CONVERSATION
//    - POST /api/morgana/conversation/{id}/end stops the manager and every actor beneath it
//    - A SignalR channel calls LeaveConversation(conversationId)
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