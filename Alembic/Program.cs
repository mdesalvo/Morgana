using Alembic.Interfaces;
using Alembic.Services;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ==============================================================================
// ALEMBIC - MORGANA'S AUTHORING WORKBENCH
// ==============================================================================
// Alembic distils a functional interview with a domain expert into a complete Morgana
// domain: intents, agent prose, tool contracts, C# assets and non-regression scenarios.
//
// What Alembic is NOT: a channel. It never calls a Morgana instance, holds no JWT, announces
// no ChannelMetadata and joins no conversation pipeline. Its only external dependency is an
// LLM. It also makes NO assumption about seeing the client's filesystem: configuration comes
// in as an upload and goes out as a download, because at runtime Alembic lives wherever the
// client deployed it — exactly like Cauldron, and for exactly the same reason.

// ============================================================================
// 1. BLAZOR SERVER CONFIGURATION
// ============================================================================
// Blazor Server provides server-side rendering with real-time UI updates via SignalR.
// The interview is long and stateful, which is precisely what a server-held circuit is good at.

builder.Services.AddRazorPages();       // Razor Pages, used only to serve _Host.cshtml
builder.Services.AddServerSideBlazor(); // Blazor Server: UI state lives here, DOM diffs go over SignalR

// ==============================================================================
// 2. LOGGING INFRASTRUCTURE
// ==============================================================================
// Several Morgana.AI services take a bare ILogger (not ILogger<T>), so one is registered here.

builder.Services.AddSingleton<ILogger>(sp =>
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Alembic"));

// ==============================================================================
// 3. MORGANA.AI SERVICES
// ==============================================================================
// Alembic reuses the framework's own prompt stack rather than reimplementing it.
//
// - IAgentConfigurationService: scans loaded assemblies for embedded agents.json. Alembic embeds
//   none and none is expected to be found: the service degrades to agentless mode, which is the
//   correct state here, because the domain Alembic works on is the one the CLIENT uploads, never
//   one compiled into this process.
// - IPromptResolverService: resolves the framework prompts from morgana.json, embedded in
//   Morgana.AI and therefore available for free through the project reference.
// - IPromptComposerService: assembles what the model reads. This is what lets Alembic show a
//   real composed prompt as the interview recap instead of a summary of the client's answers —
//   ComposeAgentInstructionsAsync takes the domain prompt as a parameter, so the Records.Prompt
//   can be built in memory from a Draft that exists nowhere on disk yet.

builder.Services.AddSingleton<IAgentConfigurationService, EmbeddedAgentConfigurationService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();
builder.Services.AddSingleton<IPromptComposerService, ConfigurationPromptComposerService>();

// ==============================================================================
// 4. ALEMBIC SERVICES
// ==============================================================================
// - IDraftImportService: parses an uploaded agents.json into a DomainDraft. Singleton: it holds
//   no per-client state, it only projects one shape onto another.
// - IDraftExportService: renders a Draft back into the agents.json Morgana loads. Together with
//   the importer it carries the invariant everything later stands on — a configuration that goes
//   in comes back out equivalent — which is what makes the interview safe to build on top.
// - IDraftValidationService: everything about a Draft decidable without asking a model. Runs
//   before the recap, because composing a beautiful prompt for a domain that would not start is
//   a way of lying to the client with something that looks like evidence.
// - IRecapService: drives IPromptComposerService over the Draft, so what the client reviews is
//   the prompt the model will really read rather than a summary of their own answers.
// - IDraftSerializationService: reads and writes alembic-draft.json, the interview's save file.
//   An interview over a real domain does not fit in one sitting, and Alembic has no database.
// - IDraftStateService: the Draft currently under construction. Scoped, which in Blazor Server
//   means one per circuit: two tabs are two separate interviews, and the state dies with the
//   connection that owns it.

builder.Services.AddSingleton<IDraftImportService, DraftImportService>();
builder.Services.AddSingleton<IDraftExportService, DraftExportService>();
builder.Services.AddSingleton<IDraftValidationService, DraftValidationService>();
builder.Services.AddSingleton<IRecapService, RecapService>();
builder.Services.AddSingleton<IDraftSerializationService, DraftSerializationService>();
builder.Services.AddScoped<IDraftStateService, DraftStateService>();

// - IAlembicPromptService: Alembic's own prose, from alembic.json embedded here the same way
//   morgana.json is embedded in Morgana.AI — same shape, same four sections. Alembic is an agent
//   of Morgana that produces agents of Morgana, so it is composed the way one is: her Personality
//   on top, resolved live rather than copied, then Alembic's doctrine, then the running pass.
//   Her GlobalPolicies, Formatting and Target are left out on purpose: they govern how a channel
//   turn is formed, and Alembic has no channel and no turn in that sense.
// - IInterviewService: the interview. Scoped — one per circuit. The state machine is C# and the
//   conducting is the model's: facts about the configuration are never left to a model's
//   discretion, and phrasing is never left to a template.

builder.Services.AddSingleton<IAlembicPromptService, AlembicPromptService>();
builder.Services.AddScoped<IInterviewService, InterviewService>();

// ==============================================================================
// 5. LLM
// ==============================================================================
// Alembic runs on the Performance tier, and the choice is not caution. Its whole job is writing
// dispositive prose that does not contradict itself — the exact task where the Efficiency die
// amplifies contradiction-following failures. A wizard that emits a subtly self-contradictory
// prompt is worse than no wizard, because the client has no instrument to notice. Alembic runs
// once, at onboarding, not per conversational turn: this is the wrong place to save.
//
// Consequence, deliberate and inherited from the framework's own "no cross-tier fallback" rule:
// Alembic does not run against a single-tier deployment (Ollama being the canonical case) until
// a Performance entry is configured.
//
// Registered as a factory and never resolved during startup, so a working copy with no LLM
// credentials still builds, boots and serves the shell — the failure surfaces where it belongs,
// on the first call, with the provider's own message.

builder.Services.AddSingleton<ILLMService>(sp =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    IPromptResolverService promptResolver = sp.GetRequiredService<IPromptResolverService>();
    ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    string llmProvider = config["Morgana:LLM:Provider"]
        ?? throw new InvalidOperationException("Morgana:LLM:Provider is not configured.");

    return llmProvider.ToLowerInvariant() switch
    {
        "anthropic"   => new Morgana.AI.Abstractions.LLMs.Anthropic(config, promptResolver, loggerFactory),
        "azureopenai" => new Morgana.AI.Abstractions.LLMs.AzureOpenAI(config, promptResolver, loggerFactory),
        "ollama"      => new Morgana.AI.Abstractions.LLMs.Ollama(config, promptResolver, loggerFactory),
        "openai"      => new Morgana.AI.Abstractions.LLMs.OpenAI(config, promptResolver, loggerFactory),
        _ => throw new InvalidOperationException($"LLM Provider '{llmProvider}' not supported. Valid values: 'Anthropic', 'AzureOpenAI', 'Ollama', 'OpenAI'")
    };
});

// ============================================================================
// 6. APPLICATION PIPELINE
// ============================================================================

WebApplication app = builder.Build();

// Production-only middleware
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");  // Global exception handler page
    app.UseHsts();                      // HTTP Strict Transport Security
}

app.UseHttpsRedirection();              // Redirect HTTP → HTTPS
app.UseStaticFiles();                   // Serve static files (CSS, JS, images)
app.UseRouting();                       // Enable endpoint routing

app.MapBlazorHub();                     // SignalR hub carrying Blazor's own UI updates
app.MapFallbackToPage("/_Host");        // Every unmatched route renders the single page

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

await app.RunAsync();
