using Distiller.Interfaces;
using Distiller.Services;
using Distiller.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ==============================================================================
// ALEMBIC - DISTILLER
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
builder.Services.AddFluentUIComponents();

// ==============================================================================
// 3. ENGINE
// ==============================================================================

builder.Services.AddSingleton<ILogger>(sp =>
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Alembic"));

builder.Services.AddSingleton<IAgentConfigurationService, AgentlessConfigurationService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();
builder.Services.AddSingleton<IPromptComposerService, ConfigurationPromptComposerService>();

builder.Services.AddSingleton<IDraftImportService, DraftImportService>();
builder.Services.AddSingleton<IDraftExportService, DraftExportService>();
builder.Services.AddSingleton<IDraftValidationService, DraftValidationService>();
builder.Services.AddSingleton<IRecapService, RecapService>();
builder.Services.AddSingleton<IDraftSerializationService, DraftSerializationService>();
builder.Services.AddScoped<IDraftStateService, DraftStateService>();

builder.Services.AddSingleton<IAlembicPromptService, AlembicPromptService>();
builder.Services.AddScoped<IInterviewService, InterviewService>();

builder.Services.AddSingleton<ISolutionEmitService, SolutionEmitService>();
builder.Services.AddSingleton<ICodeEmitService, CodeEmitService>();
builder.Services.AddSingleton<IToolMockService, ToolMockService>();
builder.Services.AddSingleton<IMigrationReportService, MigrationReportService>();

builder.Services.AddSingleton<IScenarioAuthorService, ScenarioAuthorService>();
builder.Services.AddSingleton<ICoherenceService, CoherenceService>();
builder.Services.AddSingleton<IDomainReadingService, DomainReadingService>();
builder.Services.AddSingleton<ICoherenceApplyService, CoherenceApplyService>();
builder.Services.AddSingleton<IAssetPackageService, AssetPackageService>();

// ==============================================================================
// 4. LLM
// ==============================================================================
// Performance tier, resolved on first use so a working copy without credentials still boots.

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
// 5. APPLICATION PIPELINE
// ============================================================================

WebApplication app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

DateTimeOffset startedAt = DateTimeOffset.UtcNow;
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    uptime = DateTimeOffset.UtcNow - startedAt
}));

await app.RunAsync();
