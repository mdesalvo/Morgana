using System.Globalization;
using Alembic.Interfaces;
using Alembic.Services;
using Alembic.Components;
using Alembic.Tools;
using Microsoft.FluentUI.AspNetCore.Components;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.AI.LanguageModels.Anthropic;
using Morgana.AI.LanguageModels.AzureOpenAI;
using Morgana.AI.LanguageModels.Ollama;
using Morgana.AI.LanguageModels.OpenAI;

// Invariant before anything else runs, so no thread or circuit inherits the host's locale: the
// configuration and C# Alembic emits are the same bytes whichever machine it is deployed on.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

// The host that serves the workbench.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ==============================================================================
// ALEMBIC - WORKBENCH
// ==============================================================================
// The workbench on Fluent UI 5: the landing and the import, then one shell for the interview and the
// domain. Every service states its contract in its own XML doc.

// ============================================================================
// 1. BLAZOR
// ============================================================================
// Interactive server: the interview is long and stateful, which is what a server-held circuit is for.

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// ============================================================================
// 2. FLUENT UI
// ============================================================================
// On Blazor Server the library wants a default HttpClient registered before it.

builder.Services.AddHttpClient();

// Fluent UI toasts, such as the save receipt, appear at the bottom centre of the shell.
builder.Services.AddFluentUIComponents(configuration =>
    configuration.Toast.Position = ToastPosition.BottomCenter);

// ==============================================================================
// 3. ENGINE
// ==============================================================================

// One logger named after the workbench serves every service.
builder.Services.AddSingleton<ILogger>(sp =>
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Alembic"));

// The framework's own services compose the prompts the client is shown.
// No agent is compiled into this process: the domain is the one in the draft.
builder.Services.AddSingleton<IAgentConfigurationService, AgentlessConfigurationService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();
builder.Services.AddSingleton<IPromptComposerService, ConfigurationPromptComposerService>();

// The draft and what is read off it without a model: export, validation, recap and the save file.
// The state is scoped because each circuit holds one draft.
builder.Services.AddSingleton<IDraftExportService, DraftExportService>();
builder.Services.AddSingleton<IDraftValidationService, DraftValidationService>();
builder.Services.AddSingleton<IRecapService, RecapService>();
builder.Services.AddSingleton<IDraftSerializationService, DraftSerializationService>();
builder.Services.AddScoped<IDraftStateService, DraftStateService>();

// The interview: Alembic's prompts are shared while each circuit conducts its own interview.
builder.Services.AddSingleton<IAlembicPromptService, AlembicPromptService>();
builder.Services.AddScoped<IInterviewService, InterviewService>();

// The archive the client downloads: the emitted files, the mocks, the migration report and the packaged assets.
builder.Services.AddSingleton<ISolutionEmitService, SolutionEmitService>();
builder.Services.AddSingleton<ICodeEmitService, CodeEmitService>();
builder.Services.AddSingleton<IToolMockService, ToolMockService>();
builder.Services.AddSingleton<IMigrationReportService, MigrationReportService>();

// The coherence pass that reports relational defects, the pass that applies a finding and the packaging of assets.
builder.Services.AddSingleton<ICoherenceService, CoherenceService>();
builder.Services.AddSingleton<ICoherenceApplyService, CoherenceApplyService>();
builder.Services.AddSingleton<IAssetPackageService, AssetPackageService>();

// ==============================================================================
// 4. LLM
// ==============================================================================
// Efficiency tier, resolved on first use so a working copy without credentials still boots.

// The workbench registers every provider so that a tier chooses any of them in configuration alone.
builder.Services.AddSingleton<MorganaLanguageModel, AnthropicLanguageModel>();
builder.Services.AddSingleton<MorganaLanguageModel, AzureOpenAILanguageModel>();
builder.Services.AddSingleton<MorganaLanguageModel, OpenAILanguageModel>();
builder.Services.AddSingleton<MorganaLanguageModel, OllamaLanguageModel>();

// The tiers are read from the user secrets that Morgana shares.
builder.Services.AddSingleton<ILanguageModelService>(sp =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    // The factory builds the chat client of a tier on the first model call.
    return new ConfigurationLLMService(config, sp.GetServices<MorganaLanguageModel>(), loggerFactory);
});

// ============================================================================
// 5. APPLICATION PIPELINE
// ============================================================================

// The application that serves the circuits.
WebApplication app = builder.Build();

// Alembic's tools are declared on their classes the way a domain's are, so the framework's own check
// weighs them here: a tool left without its description, its approval or its scope, or one answering
// with anything but a typed record, is a fault that nothing downstream can notice. It is asked at
// startup because left to the first pass it reaches the client as an interview that never answers on
// a host that passed its health probe.
// The framework's check runs over the two classes whose methods are the tools of the passes.
List<string> toolErrors =
[
    .. HandlesIntentAgentRegistryService.ValidateToolContract("interview", typeof(InterviewTools)),
    .. HandlesIntentAgentRegistryService.ValidateToolContract("coherence", typeof(CoherenceApplyTools))
];

// Any incomplete declaration stops the host at startup.
if (toolErrors.Count > 0)
    throw new InvalidOperationException($"Alembic's tools are not declared completely: {string.Join("; ", toolErrors)}");

// Outside development a failure shows the error page and browsers are told to insist on HTTPS.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Plain HTTP requests move to HTTPS and form posts such as the upload carry an antiforgery token.
app.UseHttpsRedirection();
app.UseAntiforgery();

// The workbench is one interactive server application, with its static assets.
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// The health probe tells a host that the workbench is up and for how long.
DateTimeOffset startedAt = DateTimeOffset.UtcNow;
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    uptime = DateTimeOffset.UtcNow - startedAt
}));

// The workbench serves until the host stops it.
await app.RunAsync();
