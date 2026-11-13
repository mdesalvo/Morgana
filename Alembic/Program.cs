using System.Globalization;
using Alembic.Interfaces;
using Alembic.Services;
using Alembic.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using Morgana.AI.LanguageModels.Anthropic;
using Morgana.AI.LanguageModels.AzureOpenAI;
using Morgana.AI.LanguageModels.Google;
using Morgana.AI.LanguageModels.Ollama;
using Morgana.AI.LanguageModels.OpenAI;
using Morgana.AI.Services;

// Invariant before anything else runs, so no thread or circuit inherits the host's locale: the
// configuration and C# Alembic emits are the same bytes whichever machine it is deployed on.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

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

// The workbench's notices appear at the bottom centre of the page.
builder.Services.AddFluentUIComponents(configuration =>
    configuration.Toast.Position = ToastPosition.BottomCenter);

// ==============================================================================
// 3. ENGINE
// ==============================================================================

// The logger the services take as a plain ILogger, its lines filed under Alembic.
builder.Services.AddSingleton<ILogger>(sp =>
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Alembic"));

// The framework's own prompt machinery over an empty domain: the domain Alembic works on is always the uploaded one,
// while the framework layer, Morgana's prompts, still resolves from the morgana.json embedded in Morgana.AI.
builder.Services.AddSingleton<IAgentConfigurationService, AgentlessConfigurationService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();
builder.Services.AddSingleton<IPromptComposerService, ConfigurationPromptComposerService>();

// The Draft in and out, checked and recapped. The Draft itself is one per circuit, so two tabs are two separate pieces of work.
builder.Services.AddSingleton<IDraftImportService, DraftImportService>();
builder.Services.AddSingleton<IDraftExportService, DraftExportService>();
builder.Services.AddSingleton<IDraftValidationService, DraftValidationService>();
builder.Services.AddSingleton<IRecapService, RecapService>();
builder.Services.AddSingleton<IDraftSerializationService, DraftSerializationService>();
builder.Services.AddScoped<IDraftStateService, DraftStateService>();

// The interview, one per circuit like the Draft it fills, conducted on Alembic's own prompts.
builder.Services.AddSingleton<IAlembicPromptService, AlembicPromptService>();
builder.Services.AddScoped<IInterviewService, InterviewService>();

// The emit: project, generated C#, mocks and the migration report.
builder.Services.AddSingleton<ISolutionEmitService, SolutionEmitService>();
builder.Services.AddSingleton<ICodeEmitService, CodeEmitService>();
builder.Services.AddSingleton<IToolMockService, ToolMockService>();
builder.Services.AddSingleton<IMigrationReportService, MigrationReportService>();

// The passes over the whole domain and the archive that packages it.
builder.Services.AddSingleton<ICoherenceService, CoherenceService>();
builder.Services.AddSingleton<IDomainReadingService, DomainReadingService>();
builder.Services.AddSingleton<ICoherenceApplyService, CoherenceApplyService>();
builder.Services.AddSingleton<IAssetPackageService, AssetPackageService>();

// ==============================================================================
// 4. LLM
// ==============================================================================
// Every pass runs on the Efficiency tier, the service resolved on first use so a working copy without credentials still boots.

// The workbench registers every provider so that a tier chooses any of them in configuration alone.
builder.Services.AddSingleton<MorganaLanguageModel, AnthropicLanguageModel>();
builder.Services.AddSingleton<MorganaLanguageModel, AzureOpenAILanguageModel>();
builder.Services.AddSingleton<MorganaLanguageModel, GoogleLanguageModel>();
builder.Services.AddSingleton<MorganaLanguageModel, OllamaLanguageModel>();
builder.Services.AddSingleton<MorganaLanguageModel, OpenAILanguageModel>();

// The tiers are read from the user secrets that Morgana shares.
builder.Services.AddSingleton<ILanguageModelService>(sp =>
{
    // What the tiers are built from: the configuration, the providers and the framework prompts.
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    IPromptResolverService promptResolver = sp.GetRequiredService<IPromptResolverService>();
    ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    // Built when the first model call resolves the service, so a tier left unconfigured is refused there rather than at boot.
    return new ConfigurationLanguageModelService(config, sp.GetServices<MorganaLanguageModel>(), promptResolver, loggerFactory);
});

// ============================================================================
// 5. APPLICATION PIPELINE
// ============================================================================

WebApplication app = builder.Build();

// Outside development a failure reaches the client as the error page and the browser is held to HTTPS.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// HTTPS only, with the antiforgery check that the Razor components' endpoints require.
app.UseHttpsRedirection();
app.UseAntiforgery();

// The workbench's own assets, then its pages on the interactive server circuit.
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// The liveness probe, with the uptime an operator reads to spot a restart loop.
DateTimeOffset startedAt = DateTimeOffset.UtcNow;
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    uptime = DateTimeOffset.UtcNow - startedAt
}));

await app.RunAsync();