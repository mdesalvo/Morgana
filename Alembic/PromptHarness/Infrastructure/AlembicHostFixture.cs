using Distiller.Interfaces;
using Distiller.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Xunit;

namespace PromptHarness.Infrastructure;

/// <summary>
/// Builds Alembic's own service graph once for the whole test assembly, the same registrations
/// <c>Program.cs</c> makes minus the Blazor Server pipeline this harness never needs.
/// </summary>
/// <remarks>
/// No host process, no port, no HTTP — unlike <c>MorganaHostFixture</c>, there is nothing here to
/// boot. Alembic is a library of services from this harness's point of view, driven directly
/// in-process, which is exactly the shape <c>IInterviewService</c> and its siblings are already
/// built to be used in (Alembic's own Blazor pages do no more than this).
/// <para>
/// Singletons — the LLM client, the prompt services, the deterministic emit services — are shared
/// across the whole run, same as <c>Program.cs</c> intends. What is Scoped in Alembic
/// (<c>IInterviewService</c>, <c>IDraftStateService</c>) must be Scoped here too: each test creates
/// its own scope via <see cref="NewScope"/> so one interview's state never leaks into another's,
/// the same isolation a fresh Blazor circuit gives two browser tabs.
/// </para>
/// </remarks>
public sealed class AlembicHostFixture : IAsyncLifetime
{
    /// <summary>The built service provider, held for the lifetime of the whole assembly run.</summary>
    private IHost host = null!;

    /// <inheritdoc />
    public ValueTask InitializeAsync()
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder();

        // Same appsettings.json Alembic itself ships, linked into this project's output (see the
        // .csproj), plus the shared user-secrets store — same UserSecretsId as Alembic, so this
        // harness always runs against whatever LLM provider and tiers Alembic is currently wired
        // to, never a copy that can drift from it.
        builder.Configuration.AddJsonFile("appsettings.json", optional: false);
        builder.Configuration.AddUserSecrets(typeof(AlembicHostFixture).Assembly, optional: true);

        builder.Services.AddSingleton<ILogger>(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger("AlembicHarness"));

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
        builder.Services.AddSingleton<ICoherenceApplyService, CoherenceApplyService>();
        builder.Services.AddSingleton<IAssetPackageService, AssetPackageService>();

        builder.Services.AddSingleton<ILLMService>(sp =>
        {
            IConfiguration config = sp.GetRequiredService<IConfiguration>();
            IPromptResolverService promptResolver = sp.GetRequiredService<IPromptResolverService>();
            ILoggerFactory loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            string llmProvider = config["Morgana:LLM:Provider"]
                ?? throw new InvalidOperationException("Morgana:LLM:Provider is not configured.");

            return llmProvider.ToLowerInvariant() switch
            {
                "anthropic" => new Morgana.AI.Abstractions.LLMs.Anthropic(config, promptResolver, loggerFactory),
                "azureopenai" => new Morgana.AI.Abstractions.LLMs.AzureOpenAI(config, promptResolver, loggerFactory),
                "ollama" => new Morgana.AI.Abstractions.LLMs.Ollama(config, promptResolver, loggerFactory),
                "openai" => new Morgana.AI.Abstractions.LLMs.OpenAI(config, promptResolver, loggerFactory),
                _ => throw new InvalidOperationException($"LLM Provider '{llmProvider}' not supported.")
            };
        });

        builder.Services.AddSingleton<Judge>();

        host = builder.Build();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        host.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// A fresh scope over Alembic's service graph — the harness's equivalent of a new browser tab:
    /// its own <see cref="IInterviewService"/>, its own <see cref="IDraftStateService"/>, sharing
    /// every singleton underneath.
    /// </summary>
    public IServiceScope NewScope() => host.Services.CreateScope();
}
