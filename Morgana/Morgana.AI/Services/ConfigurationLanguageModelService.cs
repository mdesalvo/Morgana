using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Morgana.AI.Abstractions;
using Morgana.AI.ChatClients;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>Builds the three tiers of <c>Morgana:LanguageModels:Tiers</c>, each on the provider that it declares, refusing at startup a tier that cannot be served.</summary>
public class ConfigurationLanguageModelService : ILanguageModelService
{
    /// <summary>The configuration, read for the tiers under <c>Morgana:LanguageModels</c>, the framework tier and the telemetry switches.</summary>
    private readonly IConfiguration configuration;

    /// <summary>The Morgana prompt, whose error answers a failed framework call returns to the user.</summary>
    private readonly Records.Prompt morganaPrompt;

    /// <summary>The client of each tier with the provider that built it and its dust; all three tiers are always present.</summary>
    private readonly Dictionary<Records.LLMTier, Records.ServedTier> servedTiers = new();

    /// <inheritdoc/>
    public Records.LLMTier FrameworkTier { get; }

    /// <summary>Instruments each tier's client with the <c>gen_ai.*</c> telemetry; a host that passes none serves its tiers without it.</summary>
    private readonly ILoggerFactory? loggerFactory;

    /// <summary>Records why a framework call failed, which the error answer returned in its place never says.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Charges the framework's own calls, wired after construction by <see cref="EnableDustAccounting"/>; null on a host that never wires it.
    /// The limiter depends on persistence, which is registered after the language models.
    /// </summary>
    private IDustLimitService? dustLimitService;

    /// <summary>The dust of the framework tier, which converts the token counts of the framework's calls; set with the limiter.</summary>
    private Records.MagicDustPricing? frameworkDust;

    /// <summary>
    /// Has the framework's own calls charged to the conversation's dust, once both this service and the limiter exist.
    /// An agent's calls are charged by <see cref="Adapters.MorganaAgentAdapter"/> under the agent's own role.
    /// </summary>
    public void EnableDustAccounting(IDustLimitService dustLimitService)
    {
        // From here every call of CompleteWithSystemPromptAsync is charged.
        this.dustLimitService = dustLimitService;

        // Guard, Classifier, Presenter and ChannelAdapter always run on the framework tier, so
        // their dust can be fixed once here instead of looked up on every call.
        frameworkDust = servedTiers[FrameworkTier].MorganaDust;
    }

    /// <summary>Builds the client of each of the three tiers on the provider it names.</summary>
    /// <param name="configuration">The configuration holding <c>Morgana:LanguageModels:Tiers</c> and <c>Morgana:ActorSystem:Tier</c>.</param>
    /// <param name="languageModels">The providers that the host registers: a tier is served by the one whose name it declares.</param>
    /// <param name="promptResolverService">Resolves the Morgana prompt, whose error answers a failed framework call returns.</param>
    /// <param name="loggerFactory">Instruments the tiers with telemetry; a host that passes none serves them without it.</param>
    /// <exception cref="InvalidOperationException">A tier, the provider it names, its connection, the model it runs or <c>Morgana:LanguageModels:Reliability</c> cannot be served or <c>Morgana:ActorSystem:Tier</c> names no tier.</exception>
    public ConfigurationLanguageModelService(
        IConfiguration configuration,
        IEnumerable<MorganaLanguageModel> languageModels,
        IPromptResolverService promptResolverService,
        ILoggerFactory? loggerFactory = null)
    {
        this.configuration = configuration;
        this.loggerFactory = loggerFactory;

        // A host that passes no logger factory still gets its failed calls answered, only without the record of why.
        logger = loggerFactory?.CreateLogger<ConfigurationLanguageModelService>() ?? NullLogger<ConfigurationLanguageModelService>.Instance;

        // The error answers are ready before the first call to a model.
        morganaPrompt = promptResolverService.ResolveAsync(Constants.Morgana).GetAwaiter().GetResult();

        // A tier may write the provider name in any casing.
        Dictionary<string, MorganaLanguageModel> languageModelsByProvider = new(StringComparer.OrdinalIgnoreCase);
        foreach (MorganaLanguageModel registeredLanguageModel in languageModels)
        {
            // Two providers under one name would leave the tier's choice to registration order.
            if (!languageModelsByProvider.TryAdd(registeredLanguageModel.Provider, registeredLanguageModel))
                throw new InvalidOperationException($"The provider '{registeredLanguageModel.Provider}' is registered more than once.");
        }

        // The reliability settings are shared by every tier, so they are read and checked once before the tiers.
        IConfigurationSection reliabilitySection = ResolveReliability();

        // Each tier is served by the provider it names, so a deployment may mix providers across tiers.
        foreach (Records.LLMTier llmTier in Enum.GetValues<Records.LLMTier>())
        {
            // The section of this tier is located by its key, so that every refusal can name the key a deployer writes.
            IConfigurationSection tierSection = configuration.GetSection($"Morgana:LanguageModels:Tiers:{llmTier}");

            // Who serves the tier, how it is reached, what model it runs and how they are put together.
            MorganaLanguageModel languageModel = ResolveLanguageModel(tierSection, languageModelsByProvider);
            IConfigurationSection connectionSection = ResolveConnection(tierSection, reliabilitySection, languageModel);
            Records.ModelDefinition modelDefinition = ResolveModelDefinition(tierSection, connectionSection, languageModel);
            servedTiers[llmTier] = BuildServedTier(connectionSection, languageModel, modelDefinition);
        }

        // The framework's own actors run on the tier that the deployment names.
        FrameworkTier = ResolveFrameworkTier();
    }

    /// <summary>Reads the reliability settings that every tier's connection shares.</summary>
    private IConfigurationSection ResolveReliability()
    {
        // The section is located by its key, so that every refusal can name the key a deployer writes.
        IConfigurationSection reliabilitySection = configuration.GetSection("Morgana:LanguageModels:Reliability");

        // Every provider reads both settings, so a missing one would leave a provider's own default deciding unseen.
        // NumberStyles.None refuses a sign, so a negative or zero timeout fails here too.
        string? timeoutSeconds = reliabilitySection["TimeoutSeconds"];
        if (!int.TryParse(timeoutSeconds, NumberStyles.None, CultureInfo.InvariantCulture, out int timeoutValue) || timeoutValue <= 0)
            throw new InvalidOperationException($"Morgana:LanguageModels:Reliability:TimeoutSeconds must be a positive integer but is '{timeoutSeconds}'.");

        // Zero retries is a legitimate choice but a sign is not, so a negative count fails here.
        string? maxRetries = reliabilitySection["MaxRetries"];
        if (!int.TryParse(maxRetries, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            throw new InvalidOperationException($"Morgana:LanguageModels:Reliability:MaxRetries must be zero or a positive integer but is '{maxRetries}'.");

        // The section is handed on to be laid under each tier's connection.
        return reliabilitySection;
    }

    /// <summary>Finds the provider that serves a tier and refuses a tier that names none it can be handed to.</summary>
    private static MorganaLanguageModel ResolveLanguageModel(IConfigurationSection tierSection, Dictionary<string, MorganaLanguageModel> languageModelsByProvider)
    {
        // The three tiers are all mandatory: an agent or a framework actor may be served by any of them.
        if (!tierSection.Exists())
            throw new InvalidOperationException($"{tierSection.Path} is missing. All three tiers (Economy, Efficiency, Performance) must be declared.");

        // The provider decides which client the tier is built on, so a tier without one cannot be served.
        string? provider = tierSection["Provider"];
        if (string.IsNullOrWhiteSpace(provider))
            throw new InvalidOperationException($"{tierSection.Path}:Provider is missing.");

        // Left on its placeholder the lookup would refuse it as an unknown provider, without saying it was never filled in.
        if (Constants.SecretOverrides.All.Contains(provider))
            throw new InvalidOperationException($"{tierSection.Path}:Provider is still the placeholder '{provider}'. Override it via User Secrets or environment variables before starting.");

        // An unknown name stops startup and the refusal names the tier that declared it and the names that exist.
        if (!languageModelsByProvider.TryGetValue(provider, out MorganaLanguageModel? languageModel))
            throw new InvalidOperationException($"{tierSection.Path}:Provider '{provider}' is not a registered provider. Registered: {string.Join(", ", languageModelsByProvider.Keys.Order(StringComparer.Ordinal))}.");

        // The provider is known to serve this tier.
        return languageModel;
    }

    /// <summary>Lays the shared reliability settings under a tier's connection and refuses a connection that the provider cannot use.</summary>
    private static IConfigurationSection ResolveConnection(IConfigurationSection tierSection, IConfigurationSection reliabilitySection, MorganaLanguageModel languageModel)
    {
        // The connection is bound by the provider, so a tier without the section cannot be handed to one.
        IConfigurationSection tierConnectionSection = tierSection.GetSection("Connection");
        if (!tierConnectionSection.Exists())
            throw new InvalidOperationException($"{tierSection.Path}:Connection is missing.");

        // A value on the tier would silently win over the shared one, so there would be two sources again.
        foreach (string reliabilityKey in new[] { "TimeoutSeconds", "MaxRetries" })
        {
            // The key belongs under Reliability alone: the refusal names where to write it.
            if (tierConnectionSection[reliabilityKey] is not null)
                throw new InvalidOperationException($"{tierSection.Path}:Connection:{reliabilityKey} is declared on the tier. It is declared once for every tier under Morgana:LanguageModels:Reliability:{reliabilityKey}.");
        }

        // The shared settings are laid first and the tier's own keys over them, so the provider binds the merged section
        // as its own connection and a binding error names the key under the tier's path.
        IConfigurationSection connectionSection = new ConfigurationBuilder()
            .AddInMemoryCollection(reliabilitySection.AsEnumerable(makePathsRelative: true)
                .Select(reliabilitySetting => new KeyValuePair<string, string?>($"{tierConnectionSection.Path}:{reliabilitySetting.Key}", reliabilitySetting.Value)))
            .AddInMemoryCollection(tierConnectionSection.AsEnumerable())
            .Build()
            .GetSection(tierConnectionSection.Path);

        // A connection that cannot work is refused at startup rather than at the first user turn.
        try
        {
            // The provider checks the connection it was given and a refusal is passed on with the key of the tier.
            languageModel.ValidateConnection(connectionSection);
        }
        catch (InvalidOperationException ex)
        {
            // The provider names the field and this class knows which tier it belongs to.
            throw new InvalidOperationException($"{tierSection.Path}:Connection:{ex.Message}", ex);
        }

        // The merged connection is what the provider validates here and binds again to build its client.
        return connectionSection;
    }

    /// <summary>Lays the tier over its provider's catalogue entry and refuses a model that cannot be run or priced.</summary>
    private static Records.ModelDefinition ResolveModelDefinition(IConfigurationSection tierSection, IConfigurationSection connectionSection, MorganaLanguageModel languageModel)
    {
        // A model left blank cannot be asked for anything.
        string? modelId = tierSection["Options:ModelId"];
        if (string.IsNullOrWhiteSpace(modelId))
            throw new InvalidOperationException($"{tierSection.Path}:Options:ModelId is missing.");

        // A tier left on its placeholder would otherwise build just fine, then fail on its first call.
        if (Constants.SecretOverrides.All.Contains(modelId))
            throw new InvalidOperationException($"{tierSection.Path}:Options:ModelId is still the placeholder '{modelId}'. Override it via User Secrets or environment variables before starting.");

        // The provider's entry for the model is what the tier overrides and the tier's own Pricing may stand without it.
        IConfigurationSection? cataloguedModel = languageModel.GetCataloguedModel(connectionSection, modelId);
        IConfigurationSection tierPricingSection = tierSection.GetSection("Pricing");

        // A model described neither by its provider nor by its tier would run with its dust uncounted, so it is refused at startup.
        // The catalogue lists only the models its vendor serves: a model missing from it may have been retired or may be newer
        // than this release, which Morgana cannot tell apart, so the refusal names both causes with the remedy of each.
        if (cataloguedModel is null && !tierPricingSection.Exists())
            throw new InvalidOperationException(
                $"{tierSection.Path}:Options:ModelId '{modelId}' is not in the models.json that provider '{languageModel.Provider}' ships with this release. " +
                $"If {languageModel.Provider} has retired it, choose a model that models.json lists. " +
                $"If it is newer than this release, declare its price under {tierSection.Path}:Pricing.");

        // The catalogue entry is laid under the tier's own keys, so the tier overrides it key by key and a binding error names the key a deployer writes.
        IConfigurationSection modelSection = new ConfigurationBuilder()
            .AddInMemoryCollection((cataloguedModel?.AsEnumerable(makePathsRelative: true) ?? [])
                .Select(entry => new KeyValuePair<string, string?>($"{tierSection.Path}:{entry.Key}", entry.Value)))
            .AddInMemoryCollection(tierSection.GetSection("Options").AsEnumerable().Concat(tierPricingSection.AsEnumerable()))
            .Build()
            .GetSection(tierSection.Path);

        // The merged model is priced in every direction or the tier does not start.
        foreach (string priceField in new[]
                 {
                     nameof(Records.ModelPricing.InputUsdPerMillion),
                     nameof(Records.ModelPricing.OutputUsdPerMillion),
                     nameof(Records.ModelPricing.CacheReadUsdPerMillion),
                     nameof(Records.ModelPricing.CacheWriteUsdPerMillion)
                 })
        {
            // All four prices are required: a direction left out would be charged nothing without anybody deciding so.
            string? price = modelSection[$"Pricing:{priceField}"];
            if (string.IsNullOrWhiteSpace(price))
                throw new InvalidOperationException($"{tierSection.Path}:Pricing:{priceField} is missing: neither the tier nor the models.json of provider '{languageModel.Provider}' prices model '{modelId}' in that direction.");

            // A price is a plain non-negative amount written with a dot whatever the host's culture: a placeholder or a sign fails here too.
            if (!decimal.TryParse(price, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _))
                throw new InvalidOperationException($"{tierSection.Path}:Pricing:{priceField} must be a non-negative amount in US dollars per million tokens written with a dot, but is '{price}'.");
        }

        // Every key it needs is present and a value that does not convert fails naming its full key.
        Records.ModelDefinition modelDefinition = modelSection.Get<Records.ModelDefinition>()!;

        // The ceiling bounds what one answer may cost: zero or a sign would remove or invert the bound.
        if (modelDefinition.Options.MaxOutputTokens is <= 0)
            throw new InvalidOperationException($"{tierSection.Path}:Options:MaxOutputTokens must be a positive integer but is '{modelDefinition.Options.MaxOutputTokens}'.");

        // The provider refuses the options it cannot serve, judged on the merged model rather than on one layer.
        try
        {
            // The provider sees the options exactly as the tier will send them.
            languageModel.ValidateOptions(modelDefinition.Options);
        }
        catch (InvalidOperationException ex)
        {
            // The provider names the option and this class knows which tier it belongs to.
            throw new InvalidOperationException($"{tierSection.Path}:Options:{ex.Message}", ex);
        }

        // The merged definition is what the tier runs and what it costs.
        return modelDefinition;
    }

    /// <summary>Builds the client of a tier with the defaults of its model and its telemetry around the provider's own.</summary>
    private Records.ServedTier BuildServedTier(IConfigurationSection connectionSection, MorganaLanguageModel languageModel, Records.ModelDefinition modelDefinition)
    {
        // The price of the model becomes the dust that every call on this tier is charged in.
        Records.MagicDustPricing morganaDust = languageModel.ConvertToMorganaDust(modelDefinition.Pricing);

        // The tier's chain, outermost first: the defaults layer only fills in the options that every caller left unset,
        // Microsoft.Agents.AI for domain agents and CompleteWithSystemPromptAsync for the framework alike.
        ChatClientBuilder tierBuilder = new ChatClientBuilder(languageModel.CreateChatClient(connectionSection, modelDefinition.Options))
            .ConfigureOptions(modelDefinition.Options.FillUnsetOptions);

        // Telemetry wraps the provider's client below the defaults layer.
        UseTelemetryWhenEnabled(tierBuilder);

        // The tier is served from here on: its client and the provider that built it and its price.
        return new Records.ServedTier(tierBuilder.Build(), languageModel, morganaDust);
    }

    /// <summary>Reads the tier that the framework's own actors run on.</summary>
    private Records.LLMTier ResolveFrameworkTier()
    {
        // The framework's own actors (Guard, Classifier, Presenter, ChannelAdapter) run on the tier the
        // deployment names, never on one picked for it: a missing name refuses the startup, since the first
        // turn would otherwise reach a model nobody chose.
        string frameworkTierName = configuration["Morgana:ActorSystem:Tier"] is { Length: > 0 } configuredTierName
            ? configuredTierName
            : throw new InvalidOperationException(
                "Morgana:ActorSystem:Tier is not configured. Name the tier Morgana's own actors run on: Economy, Efficiency or Performance.");

        // Only a tier's name is accepted: a number would parse as a tier just as well and say nothing to whoever reads the configuration.
        return Enum.GetValues<Records.LLMTier>()
            .Where(tier => string.Equals(tier.ToString(), frameworkTierName.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(tier => (Records.LLMTier?)tier)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Morgana:ActorSystem:Tier '{frameworkTierName}' is not a tier. Use Economy, Efficiency or Performance.");
    }

    /// <inheritdoc/>
    public IChatClient GetChatClient(Records.LLMTier tier)
        // Every tier was built at construction, so the lookup cannot miss.
        => servedTiers[tier].Client;

    /// <inheritdoc/>
    public Records.MagicDustPricing GetMorganaDust(Records.LLMTier tier)
        // Every tier was priced at construction, so the lookup cannot miss.
        => servedTiers[tier].MorganaDust;

    /// <inheritdoc/>
    public MorganaLanguageModel GetLanguageModel(Records.LLMTier tier)
        // Every tier was served at construction, so the lookup cannot miss.
        => servedTiers[tier].LanguageModel;

    /// <summary>Adds the <see cref="OpenTelemetryChatClient"/> layer to a tier when telemetry is on, so every request emits the <c>gen_ai.*</c> spans and metrics.</summary>
    private void UseTelemetryWhenEnabled(ChatClientBuilder tierBuilder)
    {
        // Without a logger factory or with telemetry switched off the tier is served bare.
        if (loggerFactory is null || !configuration.GetValue("Morgana:OpenTelemetry:Enabled", true))
            return;

        // Prompt and response bodies reach the exporters only where the deployer opted in.
        bool enableSensitiveData = configuration.GetValue("Morgana:OpenTelemetry:EnableSensitiveData", false);

        // The layer sits below the defaults layer, so the spans carry the model and ceiling that were actually sent.
        tierBuilder.UseOpenTelemetry(loggerFactory, Telemetry.LLMChatClientSourceName, otel => otel.EnableSensitiveData = enableSensitiveData);
    }

    /// <summary>
    /// Asks the framework tier one stateless question, answering with the model's text or, on a failure, with Morgana's error answer.
    /// The guard, the classifier, the presenter and the channel adapter reach a model only through here, never through a tier of their own.
    /// </summary>
    /// <param name="conversationId">The conversation that the call is attributed and charged to.</param>
    /// <param name="systemPrompt">What the framework actor asks the model to do.</param>
    /// <param name="userPrompt">The text the model works on.</param>
    /// <param name="dustRole">The role the call is charged under on the dust ledger; null charges it under the bare framework role.</param>
    /// <param name="cancellationToken">Stops the call to the model once whoever asked for it stops waiting.</param>
    public async Task<string> CompleteWithSystemPromptAsync(string conversationId, string systemPrompt, string userPrompt, string? dustRole = null, CancellationToken cancellationToken = default)
    {
        try
        {
            // Charged under the role the caller names (the ToolGuard inspection names one), otherwise under the bare framework role,
            // which tells a reader of the ledger that a charge belongs to the pipeline rather than to any agent.
            IChatClient frameworkChatClient = GetChatClient(FrameworkTier);
            IChatClient meteredChatClient = dustLimitService is not null && frameworkDust is not null
                ? new DustAccountingChatClient(frameworkChatClient, dustLimitService, frameworkDust, dustRole ?? Constants.Morgana)
                : frameworkChatClient;

            // These two messages are the entire request, with no history, no session and no tools: a framework actor
            // asks one question and is answered once. The conversation id makes the call attributable to the conversation that provoked it.
            ChatResponse response = await meteredChatClient.GetResponseAsync(
                [
                    new ChatMessage(ChatRole.System, systemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt)
                ],
                new ChatOptions
                {
                    ConversationId = conversationId
                },
                cancellationToken);

            // The framework actors parse the answer as JSON, which a model may still wrap in a markdown fence.
            return response.Text
                .Replace("```json", string.Empty)
                .Replace("```", string.Empty);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A stopped call is not a failure of the service: turned into the error text it would reach the caller as the model's answer.
            throw;
        }
        catch (Exception ex) when (GetLanguageModel(FrameworkTier).IsContentFilterRefusal(ex))
        {
            // The provider's refusal of the content is a verdict, not a failure: the guard reads it as non-compliant
            // and the classifier, the presenter and the channel adapter fall back on their own.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ConfigurationLanguageModelService: the {Tier} tier failed a framework call for conversation {ConversationId}", FrameworkTier, conversationId);

            // Any other failure answers with the error that the Morgana prompt words for a model that could not be reached.
            List<Records.ErrorAnswer> errorAnswers = morganaPrompt.GetAdditionalProperty<List<Records.ErrorAnswer>>(Constants.PromptProperties.ErrorAnswers);
            return errorAnswers.FirstOrDefault(errorAnswer => string.Equals(errorAnswer.Name, Constants.ErrorAnswers.LLMServiceError, StringComparison.OrdinalIgnoreCase))!.Content;
        }
    }
}