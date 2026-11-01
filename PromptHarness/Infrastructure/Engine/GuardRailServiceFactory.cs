using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using Morgana.AI.LanguageModels.Anthropic;
using Morgana.AI.LanguageModels.AzureOpenAI;
using Morgana.AI.LanguageModels.Google;
using Morgana.AI.LanguageModels.Ollama;
using Morgana.AI.LanguageModels.OpenAI;
using Morgana.AI.Services;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>Builds the live guard that the inspection groups measure, over the tiers the host reads.</summary>
public static class GuardRailServiceFactory
{
    /// <summary>Builds the guard over the five providers by hand: the harness host has no plugin loader.</summary>
    public static LLMGuardRailService Create(IConfiguration hostConfiguration, ILoggerFactory loggerFactory)
    {
        // The observer reads token usage off the telemetry spans and a failing run is diagnosed from what the model read.
        IConfiguration configuration = new ConfigurationBuilder()
            .AddConfiguration(hostConfiguration)
            .AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("Morgana:OpenTelemetry:Enabled", "true"),
                new KeyValuePair<string, string?>("Morgana:OpenTelemetry:EnableSensitiveData", "true")
            ])
            .Build();

        // The ScriptedDomain carries no agent: the guard resolves framework prompts only.
        IPromptResolverService promptResolver = new ConfigurationPromptResolverService(new ScriptedDomain());

        // The providers the host finds in its plugins/, built by hand so the tiers resolve as they do in the host.
        MorganaLanguageModel[] languageModels =
        [
            new AnthropicLanguageModel(loggerFactory),
            new AzureOpenAILanguageModel(loggerFactory),
            new GoogleLanguageModel(loggerFactory),
            new OllamaLanguageModel(loggerFactory),
            new OpenAILanguageModel(loggerFactory)
        ];

        // The language models of the deployment under test, exactly as the host reads them.
        ILanguageModelService languageModelService = new ConfigurationLanguageModelService(configuration, languageModels, promptResolver, loggerFactory);

        // The guard itself.
        return new LLMGuardRailService(languageModelService, promptResolver, configuration, NullLogger.Instance);
    }
}
