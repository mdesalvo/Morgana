using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Morgana.AI;
using Morgana.AI.Abstractions;
using Morgana.AI.Interfaces;
using Morgana.AI.LanguageModels.Anthropic;
using Morgana.AI.LanguageModels.AzureOpenAI;
using Morgana.AI.LanguageModels.Google;
using Morgana.AI.LanguageModels.Ollama;
using Morgana.AI.LanguageModels.OpenAI;
using Morgana.AI.Services;
using PromptHarness.Infrastructure;
using Xunit;

namespace PromptHarness.Tests;

/// <summary>
/// The group asserting how the three tiers are built from configuration: each by the provider it declares, a deployment leaving one unusable refused before any turn.
/// The service is built directly over an in-memory configuration, so no host starts and no model is reached; every key is spelled out, so a shape changing under a deployer is noticed.
/// </summary>
public sealed class LanguageModelTests
{
    /// <summary>The placeholder appsettings.json ships where a secret belongs, which counts as unconfigured.</summary>
    private const string SecurePlaceholder = "_SECURE_OVERRIDE_";

    /// <summary>The placeholder appsettings.json ships where a required non-secret belongs, which counts as unconfigured.</summary>
    private const string FunctionalPlaceholder = "_FUNCTIONAL_OVERRIDE_";

    /// <summary>The Anthropic model each tier runs in the complete configuration every test starts from.</summary>
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

    [Theory]
    [InlineData("TimeoutSeconds")]
    [InlineData("MaxRetries")]
    public void LanguageModel_A_tier_that_declares_a_reliability_setting_is_refused(string setting)
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(($"Morgana:LanguageModels:Tiers:Efficiency:Connection:{setting}", "30"))));

        Assert.Contains($"Morgana:LanguageModels:Tiers:Efficiency:Connection:{setting}", refusal.Message, StringComparison.Ordinal);
        Assert.Contains($"Morgana:LanguageModels:Reliability:{setting}", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("TimeoutSeconds", null)]
    [InlineData("TimeoutSeconds", "0")]
    [InlineData("MaxRetries", "-1")]
    [InlineData("MaxRetries", "lots")]
    public void LanguageModel_Construction_is_refused_when_a_reliability_setting_is_missing_or_malformed(string setting, string? value)
    {
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => value is null
                ? Build(Configuration(), without: $"Morgana:LanguageModels:Reliability:{setting}")
                : Build(Configuration(($"Morgana:LanguageModels:Reliability:{setting}", value))));

        Assert.Contains($"Morgana:LanguageModels:Reliability:{setting}", refusal.Message, StringComparison.Ordinal);
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
    public void LanguageModel_An_Anthropic_tier_without_an_output_ceiling_is_refused()
    {
        // A model that neither the tier nor the catalogue gives a ceiling would be sent 1024 tokens by the Anthropic adapter.
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "claude-unpriced-9"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:InputUsdPerMillion", "1"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:OutputUsdPerMillion", "1"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheReadUsdPerMillion", "1"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheWriteUsdPerMillion", "1"))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Options:MaxOutputTokens", refusal.Message, StringComparison.Ordinal);
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
        // Ollama catalogues only its cloud models, so a tier on a local model without its own Pricing has no model to run on.
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
    public void LanguageModel_An_Ollama_tier_on_ollama_com_without_its_ApiKey_is_refused()
    {
        // ollama.com bills an account, so a tier reaching it without the account's key is refused at startup.
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", "https://ollama.com"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "gemma4")),
            without: "Morgana:LanguageModels:Tiers:Economy:Connection:ApiKey"));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Connection:ApiKey", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_A_local_Ollama_tier_needs_no_ApiKey()
    {
        // A local daemon carries its own sign-in, so the tier starts without a key.
        ILanguageModelService llm = Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", "http://localhost:11434"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "phi4-mini"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:InputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:OutputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheReadUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheWriteUsdPerMillion", "0")),
            without: "Morgana:LanguageModels:Tiers:Economy:Connection:ApiKey");

        Assert.Equal(0, llm.GetMorganaDust(Records.LLMTier.Economy).InputTokensPerDustUnit);
    }

    [Fact]
    public async Task LanguageModel_An_Ollama_tier_asks_for_a_65536_token_window_by_default()
    {
        // A tier that declares no window gets the default one.
        JsonElement requestOptions = (await RecordedOllamaRequestAsync()).GetProperty("options");

        // The default window reached the daemon.
        Assert.Equal(65536, requestOptions.GetProperty("num_ctx").GetInt32());

        // A local model that neither the tier nor the catalogue gives a ceiling is sent none.
        Assert.False(requestOptions.TryGetProperty("num_predict", out _));
    }

    [Fact]
    public async Task LanguageModel_An_Ollama_tier_sends_the_options_that_it_declares()
    {
        // The ceiling, the temperature and the keys that only Ollama reads are declared on the tier.
        JsonElement request = await RecordedOllamaRequestAsync(
            ("MaxOutputTokens", "8192"), ("Temperature", "0.2"), ("AdditionalProperties:num_gpu", "2"),
            ("AdditionalProperties:use_mmap", "false"), ("AdditionalProperties:min_p", "0.05"), ("AdditionalProperties:keep_alive", "300"));
        JsonElement requestOptions = request.GetProperty("options");

        // Each declared option reached the daemon.
        Assert.Equal(8192, requestOptions.GetProperty("num_predict").GetInt32());
        Assert.Equal(2, requestOptions.GetProperty("num_gpu").GetInt32());
        Assert.Equal(0.2f, requestOptions.GetProperty("temperature").GetSingle(), 1e-6f);

        // A boolean and a decimal written in configuration reach the daemon in their own type whatever the culture.
        Assert.False(requestOptions.GetProperty("use_mmap").GetBoolean());
        Assert.Equal(0.05f, requestOptions.GetProperty("min_p").GetSingle(), 1e-6f);

        // An option that Ollama's request options do not declare reaches the daemon as written.
        Assert.Equal("300", request.GetProperty("keep_alive").GetString());
    }

    [Fact]
    public async Task LanguageModel_An_Ollama_tier_asks_for_the_window_it_declares()
    {
        // The window that the tier declares travels instead of the default one.
        JsonElement requestOptions = (await RecordedOllamaRequestAsync(("AdditionalProperties:num_ctx", "131072"))).GetProperty("options");

        // The declared window reached the daemon.
        Assert.Equal(131072, requestOptions.GetProperty("num_ctx").GetInt32());
    }

    [Fact]
    public void LanguageModel_An_Ollama_option_that_does_not_read_as_its_type_is_refused()
    {
        // A value Ollama could not read stops the startup instead of the first call.
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", "http://localhost:11434"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "phi4-mini"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:AdditionalProperties:use_mmap", "ture"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:InputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:OutputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheReadUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheWriteUsdPerMillion", "0"))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Options:AdditionalProperties:use_mmap", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("ture", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_An_Ollama_num_ctx_that_does_not_exceed_MaxOutputTokens_is_refused()
    {
        // A num_ctx no greater than MaxOutputTokens leaves no room for the prompt, so the startup stops naming both numbers.
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", "http://localhost:11434"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "phi4-mini"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:MaxOutputTokens", "8192"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:AdditionalProperties:num_ctx", "8192"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:InputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:OutputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheReadUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheWriteUsdPerMillion", "0"))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Options:AdditionalProperties:num_ctx", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("8192", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_An_Ollama_MaxOutputTokens_that_reaches_the_default_num_ctx_is_refused()
    {
        // A tier that declares no num_ctx is weighed against the default that every call asks for.
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", "http://localhost:11434"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "phi4-mini"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:MaxOutputTokens", "65536"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:InputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:OutputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheReadUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheWriteUsdPerMillion", "0"))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Options:AdditionalProperties:num_ctx", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("65536", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Morgana's default", refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://ollama.com", "mistral-large-4", "0.70", "2.10", "0.07", "0.70")]
    [InlineData("https://ollama.com", "gemma4:cloud", "0.14", "0.40", "0.05", "0.14")]
    [InlineData("http://localhost:11434", "mistral-large-4:cloud", "0.70", "2.10", "0.07", "0.70")]
    [InlineData("http://localhost:11434", "gpt-oss:120b-cloud", "0.15", "0.60", "0.014", "0.15")]
    public void LanguageModel_An_Ollama_cloud_model_is_priced_under_its_catalogue_name(
        string endpoint, string modelId, string inputUsdPerMillion, string outputUsdPerMillion, string cacheReadUsdPerMillion, string cacheWriteUsdPerMillion)
    {
        // A cloud model costs what ollama.com bills for it, whichever endpoint serves it and whichever form its name takes.
        ILanguageModelService llm = Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", endpoint),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", modelId)));
        Records.MagicDustPricing expected = new OllamaLanguageModel().ConvertToMorganaDust(new Records.ModelPricing(
            decimal.Parse(inputUsdPerMillion, CultureInfo.InvariantCulture),
            decimal.Parse(outputUsdPerMillion, CultureInfo.InvariantCulture),
            decimal.Parse(cacheReadUsdPerMillion, CultureInfo.InvariantCulture),
            decimal.Parse(cacheWriteUsdPerMillion, CultureInfo.InvariantCulture)));

        Records.MagicDustPricing dust = llm.GetMorganaDust(Records.LLMTier.Economy);

        Assert.Equal(expected.InputTokensPerDustUnit, dust.InputTokensPerDustUnit);
        Assert.Equal(expected.OutputTokensPerDustUnit, dust.OutputTokensPerDustUnit);
        Assert.Equal(expected.CachedInputWeight, dust.CachedInputWeight, 1e-9);
        Assert.Equal(expected.CacheCreationWeight, dust.CacheCreationWeight, 1e-9);
    }

    [Fact]
    public void LanguageModel_A_local_Ollama_model_named_like_a_cloud_model_is_refused_without_its_own_Pricing()
    {
        // On a daemon gpt-oss:20b runs on the deployment's own hardware, so the cloud price never applies to it.
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", "http://localhost:11434"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "gpt-oss:20b"))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("gpt-oss:20b", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_A_local_Ollama_model_named_like_a_cloud_model_runs_on_its_own_Pricing()
    {
        // The tier's own price is the one that applies to a local model.
        ILanguageModelService llm = Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", "http://localhost:11434"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "gpt-oss:20b"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:InputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:OutputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheReadUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheWriteUsdPerMillion", "0")));

        Records.MagicDustPricing dust = llm.GetMorganaDust(Records.LLMTier.Economy);

        Assert.Equal(0, dust.InputTokensPerDustUnit);
        Assert.Equal(0, dust.OutputTokensPerDustUnit);
    }

    [Fact]
    public void LanguageModel_A_partial_Pricing_of_a_tier_overlays_its_providers_own()
    {
        // Only the input price is declared: the catalogued 10 dollars for the output still applies.
        Records.MagicDustPricing dust = Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Efficiency:Pricing:InputUsdPerMillion", "1.00"))).GetMorganaDust(Records.LLMTier.Efficiency);

        Assert.Equal(15000, dust.InputTokensPerDustUnit);
        Assert.Equal(1500, dust.OutputTokensPerDustUnit);
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
        // The model is one that no catalogue lists, so no entry can fill the missing price.
        (string Key, string Value)[] prices = new[] { "InputUsdPerMillion", "OutputUsdPerMillion", "CacheReadUsdPerMillion", "CacheWriteUsdPerMillion" }
            .Where(field => field != missingField)
            .Select(field => ($"Morgana:LanguageModels:Tiers:Economy:Pricing:{field}", "1"))
            .Concat([
                ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "claude-unpriced-9"),
                ("Morgana:LanguageModels:Tiers:Economy:Options:MaxOutputTokens", "8192")
            ])
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

    [Fact]
    public void LanguageModel_An_AzureOpenAI_tier_without_its_own_Pricing_is_refused()
    {
        // Azure serves deployments that the customer names, so its models.json lists nothing and a tier must price its own.
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(() => Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "AzureOpenAI"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", "https://acme.openai.azure.com"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "acme-gpt54-prod"))));

        Assert.Contains("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("acme-gpt54-prod", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("AzureOpenAI", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguageModel_An_AzureOpenAI_tier_runs_its_deployment_on_the_Pricing_that_it_declares()
    {
        // The deployment name is the ModelId and the contract's price is the tier's own.
        ILanguageModelService llm = Build(Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "AzureOpenAI"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", "https://acme.openai.azure.com"),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "acme-gpt54-prod"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:InputUsdPerMillion", "2.75"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:OutputUsdPerMillion", "16.50"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheReadUsdPerMillion", "0.275"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheWriteUsdPerMillion", "2.75")));

        Assert.Equal("acme-gpt54-prod", MetadataOf(llm, Records.LLMTier.Economy).DefaultModelId);
        Assert.Equal(5455, llm.GetMorganaDust(Records.LLMTier.Economy).InputTokensPerDustUnit);
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

    /// <summary>Makes one call on a local Ollama Economy tier and returns the whole request that an in-process listener recorded.</summary>
    private static async Task<JsonElement> RecordedOllamaRequestAsync(params (string Key, string Value)[] tierOptions)
    {
        // Binding port 0 asks the operating system for a free port, which is released before the listener takes it.
        TcpListener portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        int freePort = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();

        // The listener plays the Ollama daemon so that the request body can be read as it left the client.
        string endpoint = $"http://127.0.0.1:{freePort}/";
        using HttpListener ollamaDaemon = new HttpListener();
        ollamaDaemon.Prefixes.Add(endpoint);
        ollamaDaemon.Start();

        // The daemon serves exactly one request and hands back what it read.
        Task<string> recordedRequestBody = Task.Run(async () =>
        {
            // The one call the tier makes below.
            HttpListenerContext daemonContext = await ollamaDaemon.GetContextAsync();

            // The body is what the client sent to the model: the options must be in it.
            using StreamReader requestReader = new StreamReader(daemonContext.Request.InputStream, Encoding.UTF8);
            string requestBody = await requestReader.ReadToEndAsync();

            // A complete one-line answer lets the client finish the call.
            byte[] answerBytes = Encoding.UTF8.GetBytes(
                "{\"model\":\"phi4-mini\",\"created_at\":\"2026-11-02T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"hi\"},\"done\":true,\"done_reason\":\"stop\"}\n");
            daemonContext.Response.StatusCode = 200;
            daemonContext.Response.ContentType = "application/x-ndjson";
            daemonContext.Response.ContentLength64 = answerBytes.Length;
            await daemonContext.Response.OutputStream.WriteAsync(answerBytes);
            daemonContext.Response.Close();

            // The body as the client sent it, for the test to read the options in.
            return requestBody;
        });

        // A local model priced at zero by its tier, with the window left to the default unless the test declares one among the options.
        Dictionary<string, string?> tierConfiguration = Configuration(
            ("Morgana:LanguageModels:Tiers:Economy:Provider", "Ollama"),
            ("Morgana:LanguageModels:Tiers:Economy:Connection:Endpoint", endpoint),
            ("Morgana:LanguageModels:Tiers:Economy:Options:ModelId", "phi4-mini"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:InputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:OutputUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheReadUsdPerMillion", "0"),
            ("Morgana:LanguageModels:Tiers:Economy:Pricing:CacheWriteUsdPerMillion", "0"));

        // The options that the test declares on the tier are the ones that must reach the daemon.
        foreach ((string key, string value) in tierOptions)
            tierConfiguration[$"Morgana:LanguageModels:Tiers:Economy:Options:{key}"] = value;

        // The call goes through the tier's whole chain exactly as an agent's would.
        ILanguageModelService llm = Build(tierConfiguration);
        await llm.GetChatClient(Records.LLMTier.Economy).GetResponseAsync("hello");

        // The recorded body is parsed and returned whole, detached from the document that owns it.
        using JsonDocument requestDocument = JsonDocument.Parse(await recordedRequestBody);
        return requestDocument.RootElement.Clone();
    }

    /// <summary>The metadata that the client of a tier reports about the model that it runs.</summary>
    private static ChatClientMetadata MetadataOf(ILanguageModelService llm, Records.LLMTier tier) =>
        llm.GetChatClient(tier).GetService<ChatClientMetadata>()
        ?? throw new InvalidOperationException($"The {tier} client reports no metadata.");

    /// <summary>Builds the service over a configuration, dropping every key under <paramref name="without"/> first.</summary>
    private static ConfigurationLanguageModelService Build(Dictionary<string, string?> values, string? without = null, MorganaLanguageModel? duplicateProvider = null)
    {
        // A test removing a whole section, such as a tier, drops every key under it.
        if (without is not null)
            foreach (string key in values.Keys.Where(key => key.StartsWith(without, StringComparison.Ordinal)).ToList())
                values.Remove(key);

        // The configuration as a host would bind it, with all five providers registered as a deployment carrying every plugin would.
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        MorganaLanguageModel[] providers = [new AnthropicLanguageModel(), new AzureOpenAILanguageModel(), new GoogleLanguageModel(), new OllamaLanguageModel(), new OpenAILanguageModel()];
        // The service reads Morgana's own prompt and no domain prompt, so the domain declares nothing.
        ConfigurationPromptResolverService promptResolver = new ConfigurationPromptResolverService(new ScriptedDomain());

        // Building it is the whole check: a refused tier throws here, as at a host's startup.
        return new ConfigurationLanguageModelService(configuration, duplicateProvider is null ? providers : [.. providers, duplicateProvider], promptResolver);
    }

    /// <summary>A complete configuration with Anthropic on every tier, with the given keys replaced.</summary>
    private static Dictionary<string, string?> Configuration(params (string Key, string Value)[] replaced)
    {
        // The framework actors on Economy and the shared reliability every tier inherits.
        Dictionary<string, string?> values = [];
        values["Morgana:ActorSystem:Tier"] = "Economy";
        values["Morgana:LanguageModels:Reliability:TimeoutSeconds"] = "60";
        values["Morgana:LanguageModels:Reliability:MaxRetries"] = "2";

        // Every tier on Anthropic with a key and a catalogued model, which builds cleanly.
        foreach (string tier in new[] { "Economy", "Efficiency", "Performance" })
        {
            values[$"Morgana:LanguageModels:Tiers:{tier}:Provider"] = "Anthropic";
            values[$"Morgana:LanguageModels:Tiers:{tier}:Connection:ApiKey"] = "test-key";
            values[$"Morgana:LanguageModels:Tiers:{tier}:Options:ModelId"] = AnthropicModels[tier];
        }

        // The keys the test changes, each the one fault or feature under test.
        foreach ((string key, string value) in replaced)
            values[key] = value;

        // The configuration, for Build to bind.
        return values;
    }
}