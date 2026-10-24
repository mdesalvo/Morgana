using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.ChatClients;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="ILanguageModelService"/>: builds the three tiers of <c>Morgana:LanguageModels:Tiers</c>, each on the provider that it declares.
/// </summary>
public class ConfigurationLanguageModelService : ILanguageModelService
{
    /// <summary>
    /// Application configuration, read for the tiers under <c>Morgana:LanguageModels</c>, the framework tier
    /// and the telemetry switches.
    /// </summary>
    private readonly IConfiguration configuration;

    /// <summary>
    /// The client of each tier with the provider that built it and its pricing. All three tiers are always present.
    /// </summary>
    private readonly Dictionary<Records.LLMTier, Records.ServedTier> servedTiers = new();

    /// <inheritdoc/>
    public Records.LLMTier FrameworkTier { get; }

    /// <summary>
    /// Logger factory used to instrument the chat client pipeline (in particular,
    /// <see cref="OpenTelemetryChatClient"/> via <see cref="UseTelemetryWhenEnabled"/>). May be
    /// <c>null</c> in test scenarios; in that case the telemetry decorator is skipped.
    /// </summary>
    private readonly ILoggerFactory? loggerFactory;

    /// <summary>
    /// Dust limiter, wired post-construction by <see cref="EnableDustAccounting"/> from
    /// Program.cs (DI ordering: the limiter depends on persistence, registered after the LLM).
    /// Null until wired or whenever dust limiting is disabled — in which case
    /// <see cref="CompleteStructuredAsync{T}"/> uses the bare chat client.
    /// </summary>
    private IDustLimitService? dustLimitService;

    /// <summary>
    /// Dust of the <see cref="FrameworkTier"/>, set alongside <see cref="dustLimitService"/>. Used to
    /// convert its token counts into dust units for framework-actor calls.
    /// </summary>
    private Records.MagicDustPricing? frameworkDust;

    /// <summary>
    /// Wires dust accounting for framework-actor LLM calls (guard, classifier, presenter,
    /// channel adapter) routed through <see cref="CompleteStructuredAsync{T}"/>. Called
    /// once from Program.cs after both this service and <see cref="IDustLimitService"/> are
    /// constructed. Agent calls are metered separately by
    /// <see cref="Adapters.MorganaAgentAdapter"/> with a per-agent role.
    /// </summary>
    public void EnableDustAccounting(IDustLimitService dustLimitService)
    {
        this.dustLimitService = dustLimitService;

        // Guard, Classifier, Presenter and ChannelAdapter always run on the framework tier, so
        // their dust can be fixed once here instead of looked up on every call.
        frameworkDust = servedTiers[FrameworkTier].MorganaDust;
    }

    /// <summary>
    /// Builds the client of each of the three tiers.
    /// </summary>
    /// <param name="configuration">Application configuration holding <c>Morgana:LanguageModels:Tiers</c> and <c>Morgana:ActorSystem:Tier</c></param>
    /// <param name="languageModels">The providers that the host registers: a tier is served by the one whose name it declares.</param>
    /// <param name="loggerFactory">
    /// Optional logger factory used to instrument the chat client with the MEAI OpenTelemetry
    /// decorator. Pass <c>null</c> to skip the decorator (test paths, unit tests).
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// A tier is missing, carries a placeholder in a field that its provider uses or declares no positive <c>MaxOutputTokens</c>.
    /// Thrown as well when a tier names a provider that is not registered or when two registered providers share a name.
    /// Thrown as well when the framework tier names no tier.
    /// </exception>
    public ConfigurationLanguageModelService(
        IConfiguration configuration,
        IEnumerable<MorganaLanguageModel> languageModels,
        ILoggerFactory? loggerFactory = null)
    {
        this.configuration = configuration;
        this.loggerFactory = loggerFactory;

        // A tier may write the provider name in any casing.
        Dictionary<string, MorganaLanguageModel> languageModelsByProvider = new(StringComparer.OrdinalIgnoreCase);
        foreach (MorganaLanguageModel registeredLanguageModel in languageModels)
        {
            // Two providers under one name would leave the tier's choice to registration order.
            if (!languageModelsByProvider.TryAdd(registeredLanguageModel.Provider, registeredLanguageModel))
                throw new InvalidOperationException($"The provider '{registeredLanguageModel.Provider}' is registered more than once.");
        }

        // Checked before binding: the binder's own message for a record constructor parameter
        // that finds no configuration does not name the key that the deployer has to add.
        foreach (Records.LLMTier tier in Enum.GetValues<Records.LLMTier>())
        {
            // The section of this tier is located by its key, so that a missing one can be named in the refusal.
            IConfigurationSection tierSection = configuration.GetSection($"Morgana:LanguageModels:Tiers:{tier}");

            // The three tiers are all mandatory: an agent or a framework actor may be served by any of them.
            if (!tierSection.Exists())
                throw new InvalidOperationException($"Morgana:LanguageModels:Tiers:{tier} is missing. All three tiers (Economy, Efficiency, Performance) must be declared.");

            // The provider decides which client the tier is built on, so a tier without one cannot be served.
            if (string.IsNullOrWhiteSpace(tierSection["Provider"]))
                throw new InvalidOperationException($"Morgana:LanguageModels:Tiers:{tier}:Provider is missing.");

            // Left on its placeholder the lookup would refuse it as an unknown provider, without saying it was never filled in.
            if (Constants.SecretOverrides.All.Contains(tierSection["Provider"]!))
                throw new InvalidOperationException(
                    $"Morgana:LanguageModels:Tiers:{tier}:Provider is still the placeholder '{tierSection["Provider"]}'. " +
                    $"Override it via User Secrets or environment variables before starting.");

            // The connection is bound by the provider, so a tier without the section cannot be handed to one.
            if (!tierSection.GetSection("Connection").Exists())
                throw new InvalidOperationException($"Morgana:LanguageModels:Tiers:{tier}:Connection is missing.");

            // Checked here because a placeholder in an int field would otherwise fail inside the binder with a message that names no key.
            string? maxOutputTokens = tierSection["Options:MaxOutputTokens"];
            if (string.IsNullOrWhiteSpace(maxOutputTokens))
                throw new InvalidOperationException($"Morgana:LanguageModels:Tiers:{tier}:Options:MaxOutputTokens is missing.");

            // A placeholder left in the ceiling would bind as nothing and let the tier run unbounded.
            if (Constants.SecretOverrides.All.Contains(maxOutputTokens))
                throw new InvalidOperationException(
                    $"Morgana:LanguageModels:Tiers:{tier}:Options:MaxOutputTokens is still the placeholder '{maxOutputTokens}'. " +
                    $"Override it via User Secrets or environment variables before starting.");

            // The ceiling bounds what one answer may cost: zero or a sign would remove or invert the bound.
            if (!int.TryParse(maxOutputTokens, NumberStyles.None, CultureInfo.InvariantCulture, out int ceiling) || ceiling <= 0)
                throw new InvalidOperationException($"Morgana:LanguageModels:Tiers:{tier}:Options:MaxOutputTokens must be a positive integer but is '{maxOutputTokens}'.");
        }

        // Every key is now known to be present and well formed, so the binder cannot fail on a missing one.
        Records.LLMTiers tiers = configuration.GetSection("Morgana:LanguageModels").Get<Records.LLMConfiguration>()!.Tiers;

        // Each tier is built from its own declaration, on the provider that it names.
        foreach (Records.LLMTier tier in Enum.GetValues<Records.LLMTier>())
        {
            // The tier as the deployer declared it: provider name and options.
            Records.TierDefinition definition = tiers.For(tier);

            // A tier left on its placeholder would otherwise bind and build just fine, then fail
            // on its first call. This is the only point where the raw value is still visible, so a tier
            // that nobody has requested yet cannot ship broken.
            if (Constants.SecretOverrides.All.Contains(definition.Options.ModelId))
                throw new InvalidOperationException(
                    $"Morgana:LanguageModels:Tiers:{tier}:Options:ModelId is still the placeholder '{definition.Options.ModelId}'. " +
                    $"Override it via User Secrets or environment variables before starting.");

            // Each tier is served by the provider it names, so a deployment may mix providers across tiers.
            // An unknown name stops startup and the refusal names the tier that declared it and the names that exist.
            if (!languageModelsByProvider.TryGetValue(definition.Provider, out MorganaLanguageModel? languageModel))
                throw new InvalidOperationException(
                    $"Morgana:LanguageModels:Tiers:{tier}:Provider '{definition.Provider}' is not a registered provider. " +
                    $"Registered: {string.Join(", ", languageModelsByProvider.Keys.Order(StringComparer.Ordinal))}.");

            // The connection belongs to the provider that the tier names: the section is handed over unbound.
            IConfigurationSection connectionSection = configuration.GetSection($"Morgana:LanguageModels:Tiers:{tier}:Connection");

            // A connection that cannot work is refused at startup rather than at the first user turn.
            try
            {
                // The provider checks the connection it was given and a refusal is passed on with the key of the tier.
                languageModel.ValidateConnection(connectionSection);
            }
            catch (InvalidOperationException ex)
            {
                // The provider names the field and this class knows which tier it belongs to.
                throw new InvalidOperationException($"Morgana:LanguageModels:Tiers:{tier}:Connection:{ex.Message}", ex);
            }

            // A model with no price would run with its dust uncounted.
            Records.MagicDustPricing? morganaDust = languageModel.ConvertToMorganaDust(definition.Options.ModelId);
            // A model that the provider does not price is refused at startup.
            if (morganaDust is null)
                throw new InvalidOperationException(
                    $"Morgana:LanguageModels:Tiers:{tier}:Options:ModelId '{definition.Options.ModelId}' has no pricing in provider '{languageModel.Provider}': a tier runs only a model whose provider prices it.");

            // The tier's chain, outermost first: the defaults layer sees (and only fills in, never overrides) the
            // ChatOptions that every caller above sent, Microsoft.Agents.AI for domain agents and
            // CompleteStructuredAsync for framework actors alike.
            // Provider SDKs disagree on null fields (the Anthropic adapter falls back to a 1024-token ceiling), hence both are filled.
            ChatOptions tierDefaultOptions = definition.Options.ToChatOptions();
            ChatClientBuilder tierBuilder = new ChatClientBuilder(languageModel.CreateChatClient(connectionSection, definition.Options))
                .ConfigureOptions(callerOptions =>
                {
                    // The callback receives a copy, so the caller's reusable options stay unchanged.
                    callerOptions.ModelId ??= tierDefaultOptions.ModelId;
                    callerOptions.MaxOutputTokens ??= tierDefaultOptions.MaxOutputTokens;
                });
            UseTelemetryWhenEnabled(tierBuilder);
            IChatClient client = tierBuilder.Build();

            // The tier is served from here on: its client and the provider that built it and its price.
            servedTiers[tier] = new Records.ServedTier(client, languageModel, morganaDust);
        }

        // The tier that serves the guard, the classifier, the presenter and the channel adapter.
        string? frameworkTierName = configuration["Morgana:ActorSystem:Tier"];

        // Left undeclared, the framework actors run on the middle tier.
        if (string.IsNullOrWhiteSpace(frameworkTierName))
            FrameworkTier = Records.LLMTier.Efficiency;

        // A tier is taken by its name in any casing. A number is not a name: it would parse to a value that serves no tier.
        else if (Enum.TryParse(frameworkTierName, ignoreCase: true, out Records.LLMTier parsedTier) && Enum.IsDefined(parsedTier))
            FrameworkTier = parsedTier;

        // Anything else names no tier, so startup stops and the refusal lists the three that exist.
        else
            throw new InvalidOperationException($"Morgana:ActorSystem:Tier '{frameworkTierName}' is not a tier. Use Economy, Efficiency or Performance.");
    }

    /// <inheritdoc/>
    public IChatClient GetChatClient(Records.LLMTier tier) => servedTiers[tier].Client;

    /// <inheritdoc/>
    public Records.MagicDustPricing GetMorganaDust(Records.LLMTier tier) => servedTiers[tier].MorganaDust;

    /// <inheritdoc/>
    public MorganaLanguageModel GetLanguageModel(Records.LLMTier tier) => servedTiers[tier].LanguageModel;

    /// <summary>
    /// Adds the MEAI <see cref="OpenTelemetryChatClient"/> layer to a tier's <paramref name="tierBuilder"/> when telemetry is enabled,
    /// so every request emits spans and metrics under the standard <c>gen_ai.*</c> semantic conventions.
    /// </summary>
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

    /// <inheritdoc/>
    public async Task<T?> CompleteStructuredAsync<T>(string conversationId, string systemPrompt, string userPrompt, JsonElement? responseSchema = null, IReadOnlyList<ChatMessage>? precedingMessages = null)
    {
        // This is how Morgana's own framework actors get an LLM, as opposed to how a
        // domain agent gets one: Guard, Classifier, Presenter and ChannelAdapter all call
        // THIS method (never GetChatClient(tier)), so they always run on the tier that
        // Morgana:ActorSystem:Tier names. They have no [RequiresLLMTier] of their own;
        // a domain agent, by contrast, is built by MorganaAgentAdapter against its own
        // declared tier via GetChatClient(tier)/GetMorganaDust(tier) and never touches this method.
        //
        // Framework-actor calls are metered under the bare framework role, which is what tells a
        // reader of the ledger that a charge belongs to the pipeline rather than to any agent.
        IChatClient frameworkChatClient = GetChatClient(FrameworkTier);
        IChatClient client = dustLimitService is not null && frameworkDust is not null
            ? new DustAccountingChatClient(frameworkChatClient, dustLimitService, frameworkDust, Constants.Morgana)
            : frameworkChatClient;

        // The provider is held to a schema: the caller's narrowed one where it gave one, else the one
        // generated from the type with the options that deserialize the answer, so the two cannot drift.
        ChatResponseFormat responseFormat = responseSchema is { } narrowedSchema
            ? ChatResponseFormat.ForJsonSchema(narrowedSchema, typeof(T).Name)
            : ChatResponseFormat.ForJsonSchema<T>(Records.DefaultJsonSerializerOptions);

        // The system prompt, the turns that the caller hands as context and the user prompt are the
        // entire request: no history, no session, no tools and nothing carried over from the caller's
        // previous call. That is what makes this method the
        // stateless half of the LLM surface — a framework actor asks one question and is answered
        // once, where an agent's turn accumulates in a session. The conversation id travels on the
        // options so the call is attributable to the conversation that provoked it.
        List<ChatMessage> requestMessages = [new ChatMessage(ChatRole.System, systemPrompt)];
        requestMessages.AddRange(precedingMessages ?? []);
        requestMessages.Add(new ChatMessage(ChatRole.User, userPrompt));
        ChatResponse response = await client.GetResponseAsync(
            requestMessages,
            new ChatOptions
            {
                ConversationId = conversationId,
                ResponseFormat = responseFormat
            });

        try
        {
            // The schema constrains the provider, not the deserializer: an answer that still does not fit the type is no answer.
            return JsonSerializer.Deserialize<T>(response.Text, Records.DefaultJsonSerializerOptions);
        }
        catch (JsonException)
        {
            // The caller owns the fallback for a missing answer; a malformed one is the same case.
            return default;
        }
    }
}