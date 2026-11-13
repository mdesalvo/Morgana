using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI;
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
    /// <param name="hostConfiguration">The configuration the host reads.</param>
    /// <param name="loggerFactory">Logger factory that the providers write to.</param>
    /// <param name="mcpClientRegistryService">The registry the guard lists an MCP server through.</param>
    /// <param name="channelMetadataStore">The channel store the guard reads the last answer of a conversation from.</param>
    public static LLMGuardRailService Create(IConfiguration hostConfiguration, ILoggerFactory loggerFactory, IMCPClientRegistryService mcpClientRegistryService, IChannelMetadataStore channelMetadataStore)
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

        // The guard under test, on the deployment's own tiers, so a verdict here is the one production would give.
        return new LLMGuardRailService(languageModelService, promptResolver, mcpClientRegistryService, channelMetadataStore, configuration, NullLogger.Instance);
    }

    /// <summary>Builds the channel store the guard reads the last answer from; no handshake is ever registered on it.</summary>
    public static ChannelMetadataStore CreateChannelMetadataStore()
    {
        // A throwaway storage with a key of its own: the store persists handshakes through it and none is ever registered here.
        SQLiteConversationPersistenceService conversationPersistenceService = new SQLiteConversationPersistenceService(
            Microsoft.Extensions.Options.Options.Create(new Records.ConversationPersistenceOptions
            {
                StoragePath = Path.Combine(Path.GetTempPath(), $"guard-channel-{Guid.NewGuid():N}"),
                EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }),
            NullLogger.Instance);

        // The store over that persistence; the empty configuration leaves every channel rule at its default.
        return new ChannelMetadataStore(conversationPersistenceService, new ConfigurationBuilder().Build(), NullLogger.Instance);
    }
}