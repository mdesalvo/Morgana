using System.Globalization;
using Alembic.Interfaces;
using Alembic.Services;
using Alembic.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Morgana.AI.Interfaces;
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
builder.Services.AddFluentUIComponents(configuration =>
    configuration.Toast.Position = ToastPosition.BottomCenter);

// ==============================================================================
// 3. ENGINE
// ==============================================================================

builder.Services.AddSingleton<ILogger>(sp =>
    sp.GetRequiredService<ILoggerFactory>().CreateLogger("Alembic"));

builder.Services.AddSingleton<IAgentConfigurationService, AgentlessConfigurationService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();
builder.Services.AddSingleton<IPromptComposerService, ConfigurationPromptComposerService>();

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

builder.Services.AddSingleton<ICoherenceService, CoherenceService>();
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
        "anthropic"   => new Morgana.AI.LanguageModels.Anthropic(config, promptResolver, loggerFactory),
        "azureopenai" => new Morgana.AI.LanguageModels.AzureOpenAI(config, promptResolver, loggerFactory),
        "ollama"      => new Morgana.AI.LanguageModels.Ollama(config, promptResolver, loggerFactory),
        "openai"      => new Morgana.AI.LanguageModels.OpenAI(config, promptResolver, loggerFactory),
        _ => throw new InvalidOperationException($"LLM Provider '{llmProvider}' not supported. Valid values: 'Anthropic', 'AzureOpenAI', 'Ollama', 'OpenAI'")
    };
});

// ============================================================================
// 5. APPLICATION PIPELINE
// ============================================================================

WebApplication app = builder.Build();

// Alembic's tools are declared on their classes the way a domain's are, so the framework's own check
// weighs them here: a tool left without its description, its approval or its scope, or one answering
// with anything but a typed record, is a fault that nothing downstream can notice. It is asked at
// startup because left to the first pass it reaches the client as an interview that never answers on
// a host that passed its health probe.
List<string> toolErrors =
[
    .. HandlesIntentAgentRegistryService.ValidateToolContract("interview", typeof(InterviewTools)),
    .. HandlesIntentAgentRegistryService.ValidateToolContract("coherence", typeof(CoherenceApplyTools))
];

if (toolErrors.Count > 0)
    throw new InvalidOperationException($"Alembic's tools are not declared completely: {string.Join("; ", toolErrors)}");

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
