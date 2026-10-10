using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting how the three tiers are built from configuration: each by the provider that it
/// declares and a deployment that leaves one unusable refused before any turn.
/// </summary>
/// <remarks>
/// Deterministic and free: <see cref="ConfigurationLLMService"/> is built directly over an in-memory configuration, so
/// no host starts and no model is reached. Every configuration key is spelled out here rather than read
/// from <c>Constants</c>, because the point is to notice a published shape changing under a deployer.
/// </remarks>
public sealed class LanguageModelTests
{
    private const string SecurePlaceholder = "_SECURE_OVERRIDE_";

    private const string FunctionalPlaceholder = "_FUNCTIONAL_OVERRIDE_";

    [Fact]
    public void LanguageModel_Each_tier_is_built_by_the_provider_it_declares()
    {
        // Three different providers, so the metadata that each client reports can only differ if each
        // tier was built by its own.
        ILLMService llm = Build(Configuration(
            ("Morgana:LLM:Tiers:Efficiency:Provider", "Ollama"),
            ("Morgana:LLM:Tiers:Efficiency:Connection:Endpoint", "http://localhost:11434"),
            ("Morgana:LLM:Tiers:Performance:Provider", "OpenAI")));

        ChatClientMetadata economy = MetadataOf(llm, Records.LLMTier.Economy);
        ChatClientMetadata efficiency = MetadataOf(llm, Records.LLMTier.Efficiency);
        ChatClientMetadata performance = MetadataOf(llm, Records.LLMTier.Performance);

        Assert.Equal("economy-model", economy.DefaultModelId);
        Assert.Equal("efficiency-model", efficiency.DefaultModelId);
        Assert.Equal("performance-model", performance.DefaultModelId);
        Assert.Equal(3, new[] { economy.ProviderName, efficiency.ProviderName, performance.ProviderName }.Distinct().Count());
    }

    [Theory]
    [InlineData("Economy")]
    [InlineData("Efficiency")]
    [InlineData("Performance")]
    public void LanguageModel_Construction_is_refused_when_a_tier_is_missing(string tier)
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(), without: $"Morgana:LLM:Tiers:{tier}"));

        Assert.Contains($"Morgana:LLM:Tiers:{tier}", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_a_tier_names_no_provider()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(), without: "Morgana:LLM:Tiers:Economy:Provider"));

        Assert.Contains("Morgana:LLM:Tiers:Economy:Provider", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_the_provider_is_a_placeholder()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(("Morgana:LLM:Tiers:Economy:Provider", FunctionalPlaceholder))));

        Assert.Contains("Morgana:LLM:Tiers:Economy:Provider", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_a_field_the_provider_uses_is_a_placeholder()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(("Morgana:LLM:Tiers:Efficiency:Connection:ApiKey", SecurePlaceholder))));

        Assert.Contains("Morgana:LLM:Tiers:Efficiency:Connection:ApiKey", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_the_model_is_a_placeholder()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(("Morgana:LLM:Tiers:Performance:Options:ModelId", FunctionalPlaceholder))));

        Assert.Contains("Morgana:LLM:Tiers:Performance:Options:ModelId", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_A_field_the_provider_does_not_use_may_stay_empty()
    {
        // Ollama reaches a local server: it has an endpoint and no key, so the key left on its
        // placeholder is nobody's concern.
        ILLMService llm = Build(Configuration(
            ("Morgana:LLM:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LLM:Tiers:Economy:Connection:Endpoint", "http://localhost:11434"),
            ("Morgana:LLM:Tiers:Economy:Connection:ApiKey", SecurePlaceholder)));

        Assert.Equal("economy-model", MetadataOf(llm, Records.LLMTier.Economy).DefaultModelId);
    }

    [Theory]
    [InlineData(null, Records.LLMTier.Efficiency)]
    [InlineData("Economy", Records.LLMTier.Economy)]
    [InlineData("performance", Records.LLMTier.Performance)]
    public void LanguageModel_Framework_tier_follows_the_actor_system_and_defaults_to_efficiency(string? configured, Records.LLMTier expected)
    {
        ILLMService llm = Build(configured is null
            ? Configuration()
            : Configuration(("Morgana:ActorSystem:Tier", configured)));

        Assert.Equal(expected, llm.FrameworkTier);
    }

    [Theory]
    [InlineData("Premium")]
    [InlineData("7")]
    public void LanguageModel_Construction_is_refused_when_the_framework_tier_is_not_a_tier(string configured)
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(("Morgana:ActorSystem:Tier", configured))));

        Assert.Contains("Morgana:ActorSystem:Tier", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_A_forced_tool_call_follows_the_provider_of_the_tier()
    {
        ILLMService llm = Build(Configuration(
            ("Morgana:LLM:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LLM:Tiers:Economy:Connection:Endpoint", "http://localhost:11434")));

        Assert.False(llm.CanForceToolCall(Records.LLMTier.Economy));
        Assert.True(llm.CanForceToolCall(Records.LLMTier.Efficiency));
    }

    /// <summary>The metadata that the client of a tier reports about the model that it runs.</summary>
    private static ChatClientMetadata MetadataOf(ILLMService llm, Records.LLMTier tier) =>
        llm.GetChatClient(tier).GetService<ChatClientMetadata>()
        ?? throw new InvalidOperationException($"The {tier} client reports no metadata.");

    /// <summary>Builds the service over a configuration, dropping every key under <paramref name="without"/> first.</summary>
    private static ConfigurationLLMService Build(Dictionary<string, string?> values, string? without = null)
    {
        if (without is not null)
            foreach (string key in values.Keys.Where(key => key.StartsWith(without, StringComparison.Ordinal)).ToList())
                values.Remove(key);

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ConfigurationLLMService(configuration, new MinimalPromptResolver());
    }

    /// <summary>A complete configuration with Anthropic on every tier, with the given keys replaced.</summary>
    private static Dictionary<string, string?> Configuration(params (string Key, string Value)[] replaced)
    {
        Dictionary<string, string?> values = [];

        foreach (string tier in new[] { "Economy", "Efficiency", "Performance" })
        {
            values[$"Morgana:LLM:Tiers:{tier}:Provider"] = "Anthropic";
            values[$"Morgana:LLM:Tiers:{tier}:Connection:ApiKey"] = "test-key";
            values[$"Morgana:LLM:Tiers:{tier}:Options:ModelId"] = $"{tier.ToLowerInvariant()}-model";
            values[$"Morgana:LLM:Tiers:{tier}:MagicDust:InputTokensPerDustUnit"] = "1000";
            values[$"Morgana:LLM:Tiers:{tier}:MagicDust:OutputTokensPerDustUnit"] = "200";
        }

        foreach ((string key, string value) in replaced)
            values[key] = value;

        return values;
    }

    /// <summary>Stands in for prompt resolution: the service reads only the framework prompt's messages.</summary>
    private sealed class MinimalPromptResolver : IPromptResolverService
    {
        public Task<Records.Prompt[]> GetAllPromptsAsync() => Task.FromResult(Array.Empty<Records.Prompt>());

        public Task<Records.Prompt> ResolveAsync(string promptID)
            => Task.FromResult(new Records.Prompt(promptID, string.Empty, string.Empty, string.Empty, null, null, "en", "1"));
    }
}
