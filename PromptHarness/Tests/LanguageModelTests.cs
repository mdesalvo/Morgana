using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using Morgana.AI.LanguageModels.Anthropic;
using Morgana.AI.LanguageModels.OpenAI;
using Morgana.AI.LanguageModels.Google;
using Morgana.AI.LanguageModels.Ollama;
using Morgana.AI.Services;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting how the three tiers are built from configuration: each by the provider that it
/// declares and a deployment that leaves one unusable refused before any turn.
/// </summary>
/// <remarks>
/// Deterministic and free: <see cref="ConfigurationLanguageModelService"/> is built directly over an in-memory configuration, so
/// no host starts and no model is reached. Every configuration key is spelled out here rather than read
/// from <c>Constants</c>, because the point is to notice a published shape changing under a deployer.
/// </remarks>
public sealed class LanguageModelTests
{
    private const string SecurePlaceholder = "_SECURE_OVERRIDE_";

    private const string FunctionalPlaceholder = "_FUNCTIONAL_OVERRIDE_";

    private static readonly Dictionary<string, string> AnthropicModels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Economy"] = "claude-haiku-5-5",
        ["Efficiency"] = "claude-sonnet-5-5",
        ["Performance"] = "claude-opus-5-5"
    };

    [Fact]
    public void LanguageModel_Each_tier_is_built_by_the_provider_it_declares()
    {
        // Three different providers, so the metadata that each client reports can only differ if each
        // tier was built by its own.
        ILanguageModelService llm = Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Efficiency:Provider", "Google"),
            ("Morgana:LanguageModels:Tiers:Efficiency:Options:ModelId", "gemini-3.5-flash-lite"),
            ("Morgana:LanguageModels:Tiers:Performance:Provider", "OpenAI"),
            ("Morgana:LanguageModels:Tiers:Performance:Options:ModelId", "gpt-6-astra")));

        ChatClientMetadata economy = MetadataOf(llm, Records.LLMTier.Economy);
        ChatClientMetadata efficiency = MetadataOf(llm, Records.LLMTier.Efficiency);
        ChatClientMetadata performance = MetadataOf(llm, Records.LLMTier.Performance);

        Assert.Equal("claude-haiku-5-5", economy.DefaultModelId);
        Assert.Equal("gemini-3.5-flash-lite", efficiency.DefaultModelId);
        Assert.Equal("gpt-6-astra", performance.DefaultModelId);
        Assert.Equal(3, new[] { economy.ProviderName, efficiency.ProviderName, performance.ProviderName }.Distinct().Count());
    }

    [Theory]
    [InlineData("Economy")]
    [InlineData("Efficiency")]
    [InlineData("Performance")]
    public void LanguageModel_Construction_is_refused_when_a_tier_is_missing(string tier)
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(), without: $"Morgana:LanguageModels:Tiers:{tier}"));

        Assert.Contains($"Morgana:LanguageModels:Tiers:{tier}", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_a_tier_names_no_provider()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(), without: "Morgana:LanguageModels:Tiers:Economy:Provider"));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Provider", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_the_provider_is_not_registered()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(("Morgana:LanguageModels:Tiers:Economy:Provider", "Gemini"))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Provider", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_a_tier_has_no_connection()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(), without: "Morgana:LanguageModels:Tiers:Economy:Connection"));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Connection", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_two_providers_share_a_name()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(), duplicateProvider: new OllamaLanguageModel()));

        Assert.Contains("Ollama", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_the_provider_is_a_placeholder()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(("Morgana:LanguageModels:Tiers:Economy:Provider", FunctionalPlaceholder))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Provider", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_a_field_the_provider_uses_is_a_placeholder()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(("Morgana:LanguageModels:Tiers:Efficiency:Connection:ApiKey", SecurePlaceholder))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Efficiency:Connection:ApiKey", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_the_model_is_a_placeholder()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(("Morgana:LanguageModels:Tiers:Performance:Options:ModelId", FunctionalPlaceholder))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Performance:Options:ModelId", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_the_output_ceiling_is_missing()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(), without: "Morgana:LanguageModels:Tiers:Efficiency:Options:MaxOutputTokens"));

        Assert.Contains("Morgana:LanguageModels:Tiers:Efficiency:Options:MaxOutputTokens", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FunctionalPlaceholder)]
    [InlineData("0")]
    [InlineData("lots")]
    public void LanguageModel_Construction_is_refused_when_the_output_ceiling_is_not_a_positive_integer(string configured)
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(("Morgana:LanguageModels:Tiers:Performance:Options:MaxOutputTokens", configured))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Performance:Options:MaxOutputTokens", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_A_field_the_provider_does_not_use_may_stay_empty()
    {
        // OpenAI reads only its key, its retries and its timeout, so an endpoint left on its placeholder is nobody's concern.
        ILanguageModelService llm = Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "OpenAI"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "gpt-5.4"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", SecurePlaceholder)));

        Assert.Equal("gpt-5.4", MetadataOf(llm, Records.LLMTier.Economy).DefaultModelId);
    }

    [Fact]
    public void LanguageModel_A_Google_tier_is_built_on_the_model_it_declares()
    {
        // Google needs only its key, which the base configuration already supplies on every tier.
        ILanguageModelService llm = Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "Google"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "gemini-3.5-flash-lite")));

        Assert.Equal("gemini-3.5-flash-lite", MetadataOf(llm, Records.LLMTier.Economy).DefaultModelId);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_the_provider_does_not_price_the_model()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "claude-unpriced-9"))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("claude-unpriced-9", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Anthropic", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_The_dust_of_a_tier_is_its_models_price_converted()
    {
        // Sonnet 5.5 at 2 and 10 dollars per million, cache read at 0.10 and 1-hour cache write at 4.00.
        Records.MagicDustPricing dust = Build(Configuration()).GetMorganaDust(Records.LLMTier.Efficiency);

        Assert.Equal(7500, dust.InputTokensPerDustUnit);
        Assert.Equal(1500, dust.OutputTokensPerDustUnit);
        Assert.Equal(0.05, dust.CachedInputWeight, 1e-9);
        Assert.Equal(2.0, dust.CacheCreationWeight, 1e-9);
    }

    [Fact]
    public void LanguageModel_An_Ollama_tier_without_its_own_Pricing_is_refused()
    {
        // Ollama ships an empty pricing.json, so a tier without its own Pricing has no model to run on.
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", "http://localhost:11434"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "llama-local"))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("llama-local", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Ollama", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_An_Ollama_tier_runs_on_the_Pricing_that_it_declares()
    {
        // A local model priced at zero by its tier: the tier starts and is charged no dust.
        ILanguageModelService llm = Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", "http://localhost:11434"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "phi4-mini"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:InputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:OutputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheReadUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheWriteUsdPerMillion", "0")));

        Records.MagicDustPricing dust = llm.GetMorganaDust(Records.LLMTier.Economy);

        Assert.Equal(0, dust.InputTokensPerDustUnit);
        Assert.Equal(0, dust.OutputTokensPerDustUnit);
        Assert.Equal(0.0, dust.CachedInputWeight, 1e-9);
        Assert.Equal(0.0, dust.CacheCreationWeight, 1e-9);
    }

    [Fact]
    public void LanguageModel_The_Pricing_of_a_tier_prevails_over_its_providers_own()
    {
        // Sonnet 5.5 is listed at 2, 10, 0.10 and 4 dollars: a negotiated 1, 5, 0.05 and 2 is what the tier is charged.
        Records.MagicDustPricing dust = Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Efficiency:Pricing:InputUsdPerMillion", "1.00"),
            ("Morgana:LanguageModels:Tiers:Efficiency:Pricing:OutputUsdPerMillion", "5.00"),
            ("Morgana:LanguageModels:Tiers:Efficiency:Pricing:CacheReadUsdPerMillion", "0.05"),
            ("Morgana:LanguageModels:Tiers:Efficiency:Pricing:CacheWriteUsdPerMillion", "2.00"))).GetMorganaDust(Records.LLMTier.Efficiency);

        Assert.Equal(15000, dust.InputTokensPerDustUnit);
        Assert.Equal(3000, dust.OutputTokensPerDustUnit);
        Assert.Equal(0.05, dust.CachedInputWeight, 1e-9);
        Assert.Equal(2.0, dust.CacheCreationWeight, 1e-9);
    }

    [Theory]
    [InlineData("InputUsdPerMillion")]
    [InlineData("OutputUsdPerMillion")]
    [InlineData("CacheReadUsdPerMillion")]
    [InlineData("CacheWriteUsdPerMillion")]
    public void LanguageModel_Construction_is_refused_when_a_declared_Pricing_lacks_a_price(string missingField)
    {
        // A direction left out would be charged nothing, so the refusal names the price to add.
        (string Key, string Value)[] prices = new[] { "InputUsdPerMillion", "OutputUsdPerMillion", "CacheReadUsdPerMillion", "CacheWriteUsdPerMillion" }
            .Where(field => field != missingField)
            .Select(field => ($"Morgana:LanguageModels:Tiers:Economy:Pricing:{field}", "1"))
            .ToArray();

        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => Build(Configuration(prices)));

        Assert.Contains($"Morgana:LanguageModels:Tiers:Economy:Pricing:{missingField}", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("free")]
    [InlineData("-1")]
    [InlineData("1,50")]
    [InlineData(FunctionalPlaceholder)]
    public void LanguageModel_Construction_is_refused_when_a_price_is_not_an_amount(string configured)
    {
        // A price is a non-negative amount written with a dot, whatever the host's culture.
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:InputUsdPerMillion", configured),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:OutputUsdPerMillion", "1"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheReadUsdPerMillion", "1"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheWriteUsdPerMillion", "1"))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Pricing:InputUsdPerMillion", refusal.Message, StringComparison.Ordinal);
        Assert.Contains(configured, refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Economy", Records.LLMTier.Economy)]
    [InlineData("efficiency", Records.LLMTier.Efficiency)]
    [InlineData("performance", Records.LLMTier.Performance)]
    public void LanguageModel_Framework_tier_follows_the_actor_system(string configured, Records.LLMTier expected)
    {
        ILanguageModelService llm = Build(Configuration(("Morgana:ActorSystem:Tier", configured)));

        Assert.Equal(expected, llm.FrameworkTier);
    }

    [Fact]
    public void LanguageModel_Construction_is_refused_when_the_framework_tier_is_missing()
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(), without: "Morgana:ActorSystem:Tier"));

        Assert.Contains("Morgana:ActorSystem:Tier", refusal.Message, StringComparison.Ordinal);
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

    /// <summary>The metadata that the client of a tier reports about the model that it runs.</summary>
    private static ChatClientMetadata MetadataOf(ILanguageModelService llm, Records.LLMTier tier) =>
        llm.GetChatClient(tier).GetService<ChatClientMetadata>()
        ?? throw new InvalidOperationException($"The {tier} client reports no metadata.");

    /// <summary>Builds the service over a configuration, dropping every key under <paramref name="without"/> first.</summary>
    private static ConfigurationLanguageModelService Build(Dictionary<string, string?> values, string? without = null, MorganaLanguageModel? duplicateProvider = null)
    {
        if (without is not null)
            foreach (string key in values.Keys.Where(key => key.StartsWith(without, StringComparison.Ordinal)).ToList())
                values.Remove(key);

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        MorganaLanguageModel[] providers = [new AnthropicLanguageModel(), new OpenAILanguageModel(), new OllamaLanguageModel(), new GoogleLanguageModel()];
        // The service reads Morgana's own prompt and no domain prompt, so the domain declares nothing.
        ConfigurationPromptResolverService promptResolver = new ConfigurationPromptResolverService(new ScriptedDomain());
        return new ConfigurationLanguageModelService(configuration, duplicateProvider is null ? providers : [.. providers, duplicateProvider], promptResolver);
    }

    /// <summary>A complete configuration with Anthropic on every tier, with the given keys replaced.</summary>
    private static Dictionary<string, string?> Configuration(params (string Key, string Value)[] replaced)
    {
        Dictionary<string, string?> values = [];
        values["Morgana:ActorSystem:Tier"] = "Economy";

        foreach (string tier in new[] { "Economy", "Efficiency", "Performance" })
        {
            values[$"Morgana:LanguageModels:Tiers:{tier}:Provider"] = "Anthropic";
            values[$"Morgana:LanguageModels:Tiers:{tier}:Connection:ApiKey"] = "test-key";
            values[$"Morgana:LanguageModels:Tiers:{tier}:Options:ModelId"] = AnthropicModels[tier];
            values[$"Morgana:LanguageModels:Tiers:{tier}:Options:MaxOutputTokens"] = "8192";
        }

        foreach ((string key, string value) in replaced)
            values[key] = value;

        return values;
    }
}
