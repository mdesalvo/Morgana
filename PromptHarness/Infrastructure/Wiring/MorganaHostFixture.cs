using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PromptHarness.Infrastructure.Engine;
using Xunit;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>
/// Boots the real Morgana host in-process on an ephemeral Kestrel port and keeps it alive for the whole test assembly.
/// The suite talks to it only over HTTP, as a channel does, while living in its process lets it listen to spans and read the log with no collector.
/// It republishes the configuration Morgana.Web resolves (appsettings and the shared user secrets) as environment variables, then overrides the storage,
/// the exporters, the limits, the guards and a per-run key for the <c>harness</c> issuer that never touches disk.
/// </summary>
public sealed class MorganaHostFixture : IAsyncLifetime
{
    /// <summary>Configuration resolved by the harness: the host's own appsettings plus the shared secrets store.</summary>
    public IConfiguration Configuration { get; private set; } = null!;

    /// <summary>Harness knobs from <c>appsettings.Harness.json</c>.</summary>
    public HarnessOptions Options { get; private set; } = null!;

    /// <summary>Base address of the host under test, such as <c>http://127.0.0.1:43117</c>.</summary>
    public string BaseAddress { get; private set; } = string.Empty;

    /// <summary>Symmetric key minted for this run and shared with the host as the <c>harness</c> issuer key.</summary>
    public string IssuerKey { get; private set; } = string.Empty;

    /// <summary>
    /// The partner this run declares, admitted to <see cref="ScopedPartnerAgent"/> and to no other agent, so the scope half of the A2A gate has something to refuse.
    /// It is declared per run rather than shipped, since a deployment carrying a partner nobody onboarded would be a worse default than the test is worth.
    /// </summary>
    public const string ScopedPartnerName = "harness-peer";

    /// <summary>The one published agent <see cref="ScopedPartnerName"/> is admitted to.</summary>
    public const string ScopedPartnerAgent = "inventory";

    /// <summary>Symmetric key minted for this run under <see cref="ScopedPartnerName"/>.</summary>
    public string ScopedPartnerKey { get; private set; } = string.Empty;

    /// <summary>
    /// The conversations <see cref="ScopedPartnerName"/> may open in an hour, high enough never to be reached.
    /// The instance under test demands a limit of every admitted partner, which is why one is declared at all.
    /// </summary>
    public const int ScopedPartnerConversationsPerHour = 100_000;

    /// <summary>
    /// The position this run's partner takes in <c>Morgana:AgentToAgent:Partners</c>, past the last entry the host's own configuration declares.
    /// The group booting a doomed host reads it to break one declaration at a time.
    /// </summary>
    public int ScopedPartnerIndex { get; private set; }

    /// <summary>
    /// A second partner admitted to the same agent but allowed one conversation an hour, so the limit can be seen doing something.
    /// The count is per issuer: meeting it on the partner every other group calls would turn every later consultation into a refusal.
    /// </summary>
    public const string MeteredPartnerName = "harness-peer-metered";

    /// <summary>Conversations <see cref="MeteredPartnerName"/> may open in an hour: the second is refused.</summary>
    public const int MeteredPartnerConversationsPerHour = 1;

    /// <summary>
    /// What <see cref="MeteredPartnerName"/> reads once it has opened its one conversation, written on that partner's own entry.
    /// A turned-away colleague is answered in this deployment's voice rather than with a status code the asking model would narrate.
    /// </summary>
    public const string MeteredPartnerRefusal = "The nursery cannot take on further exchanges with you this hour.";

    /// <summary>Symmetric key minted for this run under <see cref="MeteredPartnerName"/>.</summary>
    public string MeteredPartnerKey { get; private set; } = string.Empty;

    /// <summary>Position <see cref="MeteredPartnerName"/> takes in <c>Morgana:AgentToAgent:Partners</c>.</summary>
    public int MeteredPartnerIndex { get; private set; }

    /// <summary>The first position in <c>Morgana:AgentToAgent:Partners</c> no declaration occupies, where the doomed-host group writes its broken entry.</summary>
    public int FreePartnerIndex { get; private set; }

    /// <summary>The other installation of a federation run as the instance under test knows it: the partner name written on its one agent's attribute.</summary>
    public const string FederatedPeerName = "annex";

    /// <summary>The name that installation knows this one by, which its gate expects in the token.</summary>
    public const string FederatedCallerName = "front";

    /// <summary>The agent the other installation publishes and this one has no books for.</summary>
    public const string FederatedPeerAgent = "inventory";

    /// <summary>The other installation of this run; <c>null</c> when the run stands none up.</summary>
    public FederatedPeerHost? Peer { get; private set; }

    /// <summary>Where the other installation answers, claimed before either host starts.</summary>
    public string FederatedPeerAddress { get; private set; } = string.Empty;

    /// <summary>The one secret the two installations of a federation run share.</summary>
    public string FederationKey { get; private set; } = string.Empty;

    /// <summary>The name the poisoned partners file this installation under, which their gate would expect in the token.</summary>
    public const string PoisonedCallerName = "greenhouse";

    /// <summary>The sources a hijacking run turns against the instance under test; <c>null</c> when the run serves none.</summary>
    public PoisonedSourceHost? PoisonedSources { get; private set; }

    /// <summary>The turn budget in seconds that a cancellation run sets, short enough that a wait running out is seen quickly.</summary>
    private const int CancellableTurnSeconds = 6;

    /// <summary>The fake model every tier answers from on a cancellation run; <c>null</c> when the run reaches a real one.</summary>
    public CancellableModelHost? CancellableModel { get; private set; }

    /// <summary>The server that the shipped GardeningAgent reads its tools from; <c>null</c> when the run replaces the shipped domain.</summary>
    public GardeningServerHost? GardeningServer { get; private set; }

    /// <summary>Port the other installation binds, reserved beside this instance's own.</summary>
    private int federatedPeerPort;

    /// <summary>Slot the federated relationship occupies, read from both ends of it.</summary>
    private int FederatedPartnerIndex { get; set; }

    /// <summary>The directory of the per-conversation databases this run creates, read by the tests that look at the record itself.</summary>
    public string StoragePath => storagePath;

    /// <summary>Tee on the host's stdout; the turn observer reads tool log lines from it.</summary>
    public HostOutputCapture Output { get; private set; } = null!;

    /// <summary>The harness's own channel, already handshaken and listening for callbacks.</summary>
    public HarnessChannel Channel { get; private set; } = null!;

    /// <summary>Reader of the per-turn structural signals.</summary>
    public TurnObserver Observer { get; private set; } = null!;

    /// <summary>The provider and model bound to each tier, recorded in every journey row.</summary>
    public string LLMDescriptor { get; private set; } = string.Empty;

    /// <summary>Scenario engine bound to this host.</summary>
    public ScenarioRunner Runner { get; private set; } = null!;

    /// <summary>The judge <see cref="Runner"/> was built over, for the group that judges Morgana's presentation outside any scripted turn.</summary>
    public LLMJudge Judge { get; private set; } = null!;

    /// <summary>Logger factory handed to the judge's LLM client; deliberately silent.</summary>
    private ILoggerFactory? judgeLoggerFactory;

    /// <summary>Directory holding the per-conversation SQLite databases created during the run.</summary>
    private string storagePath = string.Empty;

    /// <summary>Fatal exception thrown by the host's entry point, republished to the first test that observes it.</summary>
    private Exception? hostFailure;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        // Step 1: resolve configuration and harness knobs before anything else needs them.
        Configuration = BuildHarnessConfiguration();
        Options = HarnessOptions.Load(Configuration);

        // Both knobs replace the whole domain, so a run holding both would have two domains claiming the one slot.
        if (Options.FederatedPeer && Options.PoisonedDomain)
            throw new InvalidOperationException("Harness:FederatedPeer and Harness:PoisonedDomain each replace the domain of the instance under test and run in separate invocations.");

        // The fake model replaces every tier, which a federation or a poisoned run configures on its own.
        if (Options.CancellableModel && (Options.FederatedPeer || Options.PoisonedDomain))
            throw new InvalidOperationException("Harness:CancellableModel rewrites every tier and runs in an invocation of its own, apart from Harness:FederatedPeer and Harness:PoisonedDomain.");

        // Step 2: keys minted for this run alone and never written to disk, plus a scratch directory for the run's conversations.
        IssuerKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        ScopedPartnerKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        MeteredPartnerKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        storagePath = Path.Combine(Path.GetTempPath(), "morgana-harness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        // Step 3: the host's port is claimed before it starts, since the channel and the health probe are built on its address.
        int port = ReserveEphemeralPort();
        BaseAddress = $"http://127.0.0.1:{port}";

        // The other installation's port is claimed in the same breath, because the instance under
        // test has to be told where its partner answers before it starts and a partner declared
        // without an address is refused at boot.
        if (Options.FederatedPeer)
        {
            // The other installation's address and the one secret both installations share.
            federatedPeerPort = ReserveEphemeralPort();
            FederatedPeerAddress = $"http://127.0.0.1:{federatedPeerPort}";
            FederationKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        }

        // The fake model must answer before the host boots, because the tiers are pointed at its address and the startup inspection of the gardening server's offer is a model call.
        if (Options.CancellableModel)
        {
            // A held streamed call sends its next line two seconds after the turn budget, so the supervisor has already stopped the turn when it does.
            CancellableModel = await CancellableModelHost.StartAsync(TimeSpan.FromSeconds(CancellableTurnSeconds + 2));
        }

        // Step 4: the resolved configuration and the harness's overrides become environment variables, which is how the host reads them.
        ApplyHostEnvironment();

        // Step 4b: on a federation run, the other installation goes up first. It inherits everything
        // just published (provider, keys, tiers) and is told only what makes it somebody else: its
        // own address and storage, the shipped domain instead of the one agent above and the partner
        // declaration admitting this instance to the agent it publishes.
        if (Options.FederatedPeer)
            Peer = await FederatedPeerHost.StartAsync(federatedPeerPort, BuildFederatedPeerEnvironment(), Options.StartupTimeoutSeconds);

        // Step 4c: on a hijacking run, the poisoned sources go up before the instance under test. Its
        // startup examines what the MCP servers offer and what the partners' cards say, so they have
        // to be answering by then.
        if (Options.PoisonedDomain)
            PoisonedSources = await PoisonedSourceHost.StartAsync();

        // Step 4d: on a run that serves the shipped domain, the gardening server goes up before the instance
        // under test, whose startup examines the server's offer. A run replacing the domain has no agent that would read it.
        if (!Options.FederatedPeer && !Options.PoisonedDomain)
            GardeningServer = await GardeningServerHost.StartAsync(Options.StartupTimeoutSeconds);

        // The tee must be in place before the host constructs its logging stack, because the
        // console logger latches Console.Out once, when its provider is created.
        Output = HostOutputCapture.Install(Options.EchoHostOutput);

        // The observer listens before the host boots: a refusal of an MCP server's offer is counted once, at startup,
        // so a listener started later would never see it.
        Observer = new TurnObserver(Output, Options.LogDrainMilliseconds);

        // Step 5: the real Morgana.Web entry point on a background thread, waited for until it answers its health endpoint or fails.
        StartHost();
        await WaitForHealthyAsync();

        // Step 6: the harness's own channel against the running host, its webhook receiver listening before any conversation starts.
        Channel = new HarnessChannel(
            BaseAddress,
            IssuerKey,
            Configuration["Morgana:Authentication:Audience"] ?? "morgana.ai",
            drainTrailingSideMessages: Options.DustBudgetPerConversation is not null);
        await Channel.StartAsync();

        // Step 7: the scenario engine last, over the channel, the observer and a judge on the instance's own tiers, whose logging stays silent.
        judgeLoggerFactory = LoggerFactory.Create(logging => logging.ClearProviders());

        // The judge's own calls are evidence of a failing run too, so its configuration records their content.
        IConfiguration judgeConfiguration = new ConfigurationBuilder()
            .AddConfiguration(Configuration)
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Morgana:OpenTelemetry:EnableSensitiveData", "true")])
            .Build();
        Judge = LLMJudge.Create(judgeConfiguration, judgeLoggerFactory);

        // The descriptor is read once so the scenario runner and the groups that write their own journey rows record the same models.
        LLMDescriptor = DescribeLlm();

        // The engine every scenario group runs through.
        Runner = new ScenarioRunner(Channel, Observer, Judge, Options, LLMDescriptor);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Torn down in reverse order of acquisition: the webhook receiver first, then the listeners and the judge's logging.
        if (Channel is not null)
            await Channel.DisposeAsync();

        Observer?.Dispose();
        judgeLoggerFactory?.Dispose();

        // The other installation is a process of its own, so unlike the host under test it really can
        // be stopped from here. It has to be, along with the conversations it was asked to open.
        if (Peer is not null)
            await Peer.DisposeAsync();

        // The poisoned sources hold fixed ports, so they have to be released for the next invocation.
        if (PoisonedSources is not null)
            await PoisonedSources.DisposeAsync();

        // The fake model holds a port of the process, so it is released with the rest.
        if (CancellableModel is not null)
            await CancellableModel.DisposeAsync();

        // The gardening server holds the fixed port that the shipped agent names, so it has to be released for the next invocation.
        if (GardeningServer is not null)
            await GardeningServer.DisposeAsync();

        // The host runs on a background thread and stops with the test process; there is no
        // lifetime handle to signal from here. Only the throwaway databases are ours to clean up.
        try
        {
            // The run's databases are throwaway.
            if (Directory.Exists(storagePath))
                Directory.Delete(storagePath, recursive: true);
        }
        catch (IOException)
        {
            // A database file still held open by the host is not worth failing a run over.
        }
    }

    /// <summary>
    /// Renders the provider and the model bound to each tier, such as <c>Economy=Anthropic/claude-haiku-5-5</c>, recorded in every journey file.
    /// A token count is only comparable against the same models.
    /// </summary>
    private string DescribeLlm()
    {
        // Each tier with its provider and model, so a journey row reads without the configuration beside it.
        IEnumerable<string> tiers = Configuration.GetSection("Morgana:LanguageModels:Tiers").GetChildren()
            .Select(tier => $"{tier.Key}={tier["Provider"] ?? "(unset)"}/{tier["Options:ModelId"] ?? "(unset)"}");

        // The three tiers on one line.
        return string.Join(", ", tiers);
    }

    /// <summary>Resolves the configuration the host resolves: its <c>appsettings.json</c>, the harness knobs, the shared user secrets and the environment.</summary>
    private static IConfiguration BuildHarnessConfiguration()
        // Each layer overrides the one before: the host's appsettings linked into this output, the harness's knobs, the secrets
        // holding the provider, keys and tiers, then the environment, so a CI runner's variables win last.
        => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Harness.json", optional: false)
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

    /// <summary>Publishes the resolved configuration to the host process-wide, then layers the harness overrides on top.</summary>
    private void ApplyHostEnvironment()
    {
        // Every resolved "Morgana:" key becomes an environment variable in the double-underscore form, so the host reads exactly the
        // provider, keys and tiers the harness resolved, under any runner, without reading the secrets store again.
        foreach (KeyValuePair<string, string?> entry in Configuration.AsEnumerable())
        {
            // A section heading carries no value; keys outside Morgana: are the harness's own.
            if (entry.Value is null || !entry.Key.StartsWith("Morgana:", StringComparison.Ordinal))
                continue;

            Environment.SetEnvironmentVariable(entry.Key.Replace(":", "__"), entry.Value);
        }

        // From here on, each line is a harness-specific override layered on top of the inherited
        // configuration above — never something the instance under test would run with unattended.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("Morgana__ConversationPersistence__StoragePath", storagePath);

        // The shipped domain, deployed beside this suite rather than in the directory scanned by
        // default: naming it here is what lets a run that swaps the domain leave it out, since the
        // default directory is always scanned and every domain found there is merged into the installation.
        Environment.SetEnvironmentVariable("Morgana__Plugins__Directories__0", "domain-plugins");

        // The two guards stay off unless a run asks for them, since every guarded turn or question costs one more model call.
        Environment.SetEnvironmentVariable("Morgana__ActorSystem__EnableUserGuardrail", Options.EnableUserGuardrail ? "true" : "false");
        Environment.SetEnvironmentVariable("Morgana__ActorSystem__EnablePeerGuardrail", Options.EnablePeerGuardrail ? "true" : "false");

        // Every LLM span carries what the model was handed and answered, so a failing run keeps its own
        // evidence. The exporters are off, so none of it leaves the process.
        Environment.SetEnvironmentVariable("Morgana__OpenTelemetry__EnableSensitiveData", "true");

        // Unset by default: only RateLimitTests sets it, in its own filtered dotnet test invocation, so no
        // other group's conversation is ever refused for calling too often.
        Environment.SetEnvironmentVariable("Morgana__RateLimiting__Enabled", Options.RateLimitPerMinute is null ? "false" : "true");
        if (Options.RateLimitPerMinute is { } rateLimitPerMinute)
            Environment.SetEnvironmentVariable("Morgana__RateLimiting__MaxMessagesPerMinute", rateLimitPerMinute.ToString(CultureInfo.InvariantCulture));

        // Unset by default: only DustTests sets it, in its own invocation, so every other group runs with no dust limit at all.
        if (Options.DustBudgetPerConversation is { } dustBudget)
        {
            // The ledger on, with the small budget the dust group crosses in a few turns.
            Environment.SetEnvironmentVariable("Morgana__DustLimiting__Enabled", "true");
            Environment.SetEnvironmentVariable("Morgana__DustLimiting__BudgetPerConversation", dustBudget.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            // No ledger, so no conversation of a repeated suite is ever locked out.
            Environment.SetEnvironmentVariable("Morgana__DustLimiting__Enabled", "false");
        }

        // The example plugin's InventoryTool reads this to pin the order's seal word, which a scripted scenario must recite back
        // in a later turn since no scenario can carry a value the model invents into a later say:.
        Environment.SetEnvironmentVariable("Harness__DeterministicSealWord", Options.DeterministicSealWord);

        // Unset by default: only SummarizationTests sets these, in its own invocation, so every other group runs on the inherited reducer.
        if (Options.SummarizationThreshold is { } summarizationThreshold)
            Environment.SetEnvironmentVariable("Morgana__HistoryReducer__SummarizationThreshold", summarizationThreshold.ToString(CultureInfo.InvariantCulture));
        if (Options.SummarizationTargetCount is { } summarizationTargetCount)
            Environment.SetEnvironmentVariable("Morgana__HistoryReducer__SummarizationTargetCount", summarizationTargetCount.ToString(CultureInfo.InvariantCulture));

        // The spans stay on for the in-process listener, while every configured exporter is switched off by its own index:
        // an OTLP exporter would flood a collector that may not be listening.
        int exporterIndex = 0;
        foreach (IConfigurationSection _ in Configuration.GetSection("Morgana:OpenTelemetry:Exporters").GetChildren())
            Environment.SetEnvironmentVariable($"Morgana__OpenTelemetry__Exporters__{exporterIndex++}__Enabled", "false");

        // The harness channel is declared here alone, past the last issuer of the host's appsettings, with a key minted for this run:
        // a deployed installation never carries the instrument's identity. The ring of the host's own agents is coined at startup
        // and configured nowhere, so it is not written here.
        int harnessIssuerIndex = Configuration.GetSection("Morgana:Authentication:Issuers").GetChildren().Count();
        Environment.SetEnvironmentVariable($"Morgana__Authentication__Issuers__{harnessIssuerIndex}__Name", HarnessChannel.IssuerName);
        Environment.SetEnvironmentVariable($"Morgana__Authentication__Issuers__{harnessIssuerIndex}__SymmetricKey", IssuerKey);

        // One partner past the last index of the host's appsettings, admitted to a single agent: refusing an unauthenticated call proves
        // the door is shut, while this proves it is shut selectively. Its key, its reach and its limit are one entry.
        ScopedPartnerIndex = Configuration.GetSection("Morgana:AgentToAgent:Partners").GetChildren().Count();
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__Name", ScopedPartnerName);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__SymmetricKey", ScopedPartnerKey);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__InboundPolicy__Enabled", "true");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__InboundPolicy__OnAgents__0", ScopedPartnerAgent);

        // The limit every admitted partner must declare, far above anything a scripted suite reaches: it satisfies the declaration and is never met.
        Environment.SetEnvironmentVariable(
            $"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__InboundPolicy__RateLimiting__Enabled", "true");
        Environment.SetEnvironmentVariable(
            $"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__InboundPolicy__RateLimiting__MaxConversationsPerHour",
            ScopedPartnerConversationsPerHour.ToString(CultureInfo.InvariantCulture));

        // The same admission with a limit that can be met and the sentence a turned-away partner reads: one conversation an hour lets
        // the first exchange be served, which makes the second one's refusal mean something.
        MeteredPartnerIndex = ScopedPartnerIndex + 1;
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__Name", MeteredPartnerName);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__SymmetricKey", MeteredPartnerKey);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__InboundPolicy__Enabled", "true");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__InboundPolicy__OnAgents__0", ScopedPartnerAgent);
        Environment.SetEnvironmentVariable(
            $"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__InboundPolicy__RateLimiting__Enabled", "true");
        Environment.SetEnvironmentVariable(
            $"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__InboundPolicy__RateLimiting__MaxConversationsPerHour",
            MeteredPartnerConversationsPerHour.ToString(CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable(
            $"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__InboundPolicy__RateLimiting__ErrorMessagePerHour",
            MeteredPartnerRefusal);

        // Where a broken declaration may be written without landing on either of the two above.
        FreePartnerIndex = MeteredPartnerIndex + 1;

        // On a federation run the instance under test knows one more partner (a whole other
        // installation) while its own domain is replaced by the single agent that holds a colleague
        // there. Off, none of this is written and the instance is the one every other group drives.
        if (Options.FederatedPeer)
            ApplyFederationEnvironment();

        // On a hijacking run the domain is replaced by the one agent that consults the poisoned sources.
        if (Options.PoisonedDomain)
            ApplyPoisonedDomainEnvironment();

        // On a cancellation run every tier answers from the fake model and the waits are short enough to watch run out.
        if (Options.CancellableModel)
            ApplyCancellableModelEnvironment();

        // Framework categories at Information, everything else quiet: the tool log lines the turn
        // observer parses are Information-level and the rest is noise in the capture buffer.
        Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Morgana", Options.HostLogLevel);
    }

    /// <summary>Points the three tiers at the fake model and sets the turn budget, the guards and the dust ledger that the cancellation group measures.</summary>
    private void ApplyCancellableModelEnvironment()
    {
        // Every tier answers from the fake model, whichever provider the inherited configuration names.
        foreach (string tier in new[] { "Economy", "Efficiency", "Performance" })
        {
            Environment.SetEnvironmentVariable($"Morgana__LanguageModels__Tiers__{tier}__Provider", "Ollama");
            Environment.SetEnvironmentVariable($"Morgana__LanguageModels__Tiers__{tier}__Connection__Endpoint", CancellableModel!.Address);
            Environment.SetEnvironmentVariable($"Morgana__LanguageModels__Tiers__{tier}__Connection__ApiKey", "cancellable-model");
            Environment.SetEnvironmentVariable($"Morgana__LanguageModels__Tiers__{tier}__Options__ModelId", "beam");

            // A model on a local endpoint is never priced from the catalogue, so each tier declares the price that makes a completed call cost dust.
            foreach (string priceKey in new[] { "InputUsdPerMillion", "OutputUsdPerMillion", "CacheReadUsdPerMillion", "CacheWriteUsdPerMillion" })
                Environment.SetEnvironmentVariable($"Morgana__LanguageModels__Tiers__{tier}__Pricing__{priceKey}", "1");
        }

        // A turn budget short enough that a wait running out is seen in seconds.
        Environment.SetEnvironmentVariable("Morgana__ActorSystem__TimeoutSeconds", CancellableTurnSeconds.ToString(CultureInfo.InvariantCulture));

        // Both guards judge, since a held guard call is half of what the group stops.
        Environment.SetEnvironmentVariable("Morgana__ActorSystem__EnableUserGuardrail", "true");
        Environment.SetEnvironmentVariable("Morgana__ActorSystem__EnablePeerGuardrail", "true");

        // The ledger is on, with a budget no scripted conversation can reach, so that what is charged can be counted.
        Environment.SetEnvironmentVariable("Morgana__DustLimiting__Enabled", "true");
        Environment.SetEnvironmentVariable("Morgana__DustLimiting__BudgetPerConversation", "1000000");
    }

    /// <summary>Asks the system for a free TCP port by binding and releasing it, since the harness needs the number before the host binds it.</summary>
    private static int ReserveEphemeralPort()
    {
        // The port is freed again for Kestrel to bind moments later: a brief race, safe in practice, rather than a true reservation.
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        // The number the host and the channel are built on.
        return port;
    }

    /// <summary>
    /// Invokes <c>Morgana.Web</c>'s entry point on a background thread.
    /// The content root is this project's output, which holds the linked <c>appsettings.json</c> and the plugin directories the loader scans.
    /// </summary>
    private void StartHost()
    {
        // The assembly of the global-namespace Program the compiler resolved, which must be Morgana.Web's.
        Assembly hostAssembly = typeof(Program).Assembly;

        // A same-named type in this assembly would win the lookup silently and the harness would re-enter the test runner's own entry point.
        if (hostAssembly.GetName().Name != "Morgana.Web")
            throw new InvalidOperationException(
                $"Expected the global Program type to come from Morgana.Web, but it came from {hostAssembly.GetName().Name}.");

        // The host's Main, invoked directly.
        MethodInfo entryPoint = hostAssembly.EntryPoint
            ?? throw new InvalidOperationException("Morgana.Web exposes no entry point.");

        // The arguments a launch would have passed, since no process boundary supplies them here.
        string[] arguments =
        [
            "--urls", BaseAddress,
            "--environment", "Development",
            "--contentRoot", AppContext.BaseDirectory,

            // Load-bearing. ApplicationName defaults to the entry assembly, which under a test
            // runner is the runner itself, while MVC discovers controllers from the application
            // part named by it. Without this the host starts, serves and answers 404 to every
            // route, because it never found a controller.
            "--applicationName", hostAssembly.GetName().Name!
        ];

        // The host runs on its own thread, since its Main blocks until Kestrel stops and InitializeAsync must go on to probe its health.
        Thread hostThread = new Thread(() =>
        {
            try
            {
                // The entry point is asynchronous: waiting on its task is how a startup failure surfaces instead of vanishing on a thread nobody joins.
                if (entryPoint.Invoke(null, [arguments]) is Task hostTask)
                    hostTask.GetAwaiter().GetResult();
            }
            catch (TargetInvocationException ex)
            {
                // The host's own startup error, unwrapped, is what WaitForHealthyAsync reports.
                hostFailure = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                // A failure thrown through the task arrives unwrapped.
                hostFailure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "morgana-harness-host"
        };

        // A background thread, so the host never keeps the test process alive after the run.
        hostThread.Start();
    }

    /// <summary>
    /// Polls the unauthenticated health endpoint until the host answers or the startup budget runs out.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the host failed to start, with its own startup error as the inner exception.</exception>
    private async Task WaitForHealthyAsync()
    {
        // A probe that answers or fails within seconds, so a slow boot is retried rather than waited on.
        using HttpClient httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri(BaseAddress);
        httpClient.Timeout = TimeSpan.FromSeconds(5);

        // What the last probe saw, reported if the budget runs out.
        string lastProbe = "never answered";

        // The boot (plugin scan, registry, tier checks) offers no ready signal, so the probe repeats until the budget runs out.
        DateTime deadline = DateTime.UtcNow.AddSeconds(Options.StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            // A boot that refused is reported at once, with its own error, rather than after the rest of the budget.
            if (hostFailure is not null)
                throw new InvalidOperationException(
                    "Morgana failed to start. Startup validation is fail-fast: check the LLM provider, its Tiers map and " +
                    $"the agent/intent registry against the shared user-secrets store.{FormatHostOutput()}", hostFailure);

            try
            {
                // A healthy answer ends the wait: the actor system runs and the controllers are mapped.
                using HttpResponseMessage response = await httpClient.GetAsync("/api/morgana/health");
                if (response.IsSuccessStatusCode)
                    return;

                // Up but not healthy: the answer is kept so a timeout shows the last thing the host said.
                lastProbe = $"HTTP {(int)response.StatusCode} — {await response.Content.ReadAsStringAsync()}";
            }
            catch (HttpRequestException ex)
            {
                // The window before Kestrel has bound the port, which is no failure yet.
                lastProbe = $"{ex.GetType().Name}: {ex.Message}";
            }
            catch (TaskCanceledException)
            {
                // A probe that outlived its own timeout; the next one decides.
                lastProbe = "request timed out";
            }

            // The next probe follows after a short pause.
            await Task.Delay(250);
        }

        // The budget is spent: the last probe and the host's own output say why.
        throw new TimeoutException(
            $"Morgana did not become healthy within {Options.StartupTimeoutSeconds}s at {BaseAddress}. " +
            $"Last probe: {lastProbe}.{FormatHostOutput()}");
    }

    /// <summary>
    /// The tail of the captured host output, appended to startup failures.
    /// The capture swallows the host's stdout, so without it a startup error would surface as a bare timeout.
    /// </summary>
    private string FormatHostOutput()
    {
        // The whole capture from the run's start, since a startup failure has no turn to scope it to.
        IReadOnlyList<string> lines = Output.Since(0);

        // Nothing at all means the entry point probably never ran.
        if (lines.Count == 0)
            return "\n\nThe host produced no output at all — its entry point may never have run.";

        // The last 60 lines show the startup error without the whole boot in every message.
        return $"\n\n--- host output (last {Math.Min(lines.Count, 60)} of {lines.Count} lines) ---\n"
             + string.Join("\n", lines.TakeLast(60));
    }

    /// <summary>
    /// Tells the instance under test that it holds one agent of its own and one colleague abroad.
    /// The domain is replaced rather than added to: an agent answering from its own greenhouse would prove nothing about a colleague across the wire.
    /// </summary>
    private void ApplyFederationEnvironment()
    {
        // The one domain this instance holds on this run, in place of the shipped one. Replaced and
        // not added to: an installation reads one agents.json, so two deployed domains would race
        // and the agent declaring a colleague abroad would be the one whose intents went missing.
        Environment.SetEnvironmentVariable("Morgana__Plugins__Directories__0", "federation-plugins");

        // The partner the plugin's own attribute names. Its address is where the other installation
        // was told to answer and the issuer is the name that installation filed this one under —
        // which no card can carry, being the name of one caller among however many it has.
        FederatedPartnerIndex = FreePartnerIndex;

        // The partner's whole entry: who it is, where it answers, the shared key and the outbound direction this instance calls it on.
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__Name", FederatedPeerName);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__Url", FederatedPeerAddress);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__SymmetricKey", FederationKey);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__Enabled", "true");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__OutboundPolicy__Enabled", "true");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__OutboundPolicy__Issuer", FederatedCallerName);

        // The two partners declared above this one are admitted to an agent the shipped domain
        // publishes and this domain does not, which startup refuses. They belong to the groups that
        // drive the instance as itself, so on this run they are parked rather than corrected.
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__Enabled", "false");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__Enabled", "false");

        // Past the entry just written, so a doomed-boot case still lands on a slot nobody declared.
        FreePartnerIndex = FederatedPartnerIndex + 1;
    }

    /// <summary>
    /// Tells the instance under test that it holds one agent of its own and two partners that answer with an attempt at hijacking.
    /// The domain is replaced as federation replaces it: the one agent must be the only one that can answer, so what it does with each source is all there is to see.
    /// </summary>
    private void ApplyPoisonedDomainEnvironment()
    {
        // The one domain this instance holds on this run, in place of the shipped one.
        Environment.SetEnvironmentVariable("Morgana__Plugins__Directories__0", "poisoned-plugins");

        // The two partners the plugin's attributes name: both answer at the sources' address and each
        // carries a key of its own, since they are two installations as far as this one can tell.
        int hedgerowPartnerIndex = FreePartnerIndex;
        int quincePartnerIndex = FreePartnerIndex + 1;

        // The hedging nursery, whose answers try to harvest the customer's details.
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{hedgerowPartnerIndex}__Name", PoisonedSourceHost.HedgerowPartnerName);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{hedgerowPartnerIndex}__Url", PoisonedSourceHost.SourcesAddress);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{hedgerowPartnerIndex}__SymmetricKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{hedgerowPartnerIndex}__Enabled", "true");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{hedgerowPartnerIndex}__OutboundPolicy__Enabled", "true");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{hedgerowPartnerIndex}__OutboundPolicy__Issuer", PoisonedCallerName);

        // The fruit orchard, whose card asks for the customer's card number and is refused at startup.
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{quincePartnerIndex}__Name", PoisonedSourceHost.QuincePartnerName);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{quincePartnerIndex}__Url", PoisonedSourceHost.SourcesAddress);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{quincePartnerIndex}__SymmetricKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{quincePartnerIndex}__Enabled", "true");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{quincePartnerIndex}__OutboundPolicy__Enabled", "true");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{quincePartnerIndex}__OutboundPolicy__Issuer", PoisonedCallerName);

        // The scoped and metered partners are admitted to agents of the shipped domain that this one
        // does not publish, which startup refuses: they are parked as federation parks them.
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__Enabled", "false");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__Enabled", "false");

        // Past both entries just written, so a doomed-boot case still lands on a slot nobody declared.
        FreePartnerIndex = quincePartnerIndex + 1;
    }

    /// <summary>
    /// Tells the other installation who it is: the shipped domain with one partner admitted to the agent this instance has no books for.
    /// Everything else it inherits from the environment just published, so the two are two configurations of one deployment.
    /// </summary>
    private Dictionary<string, string> BuildFederatedPeerEnvironment()
        => new Dictionary<string, string>
        {
            // The shipped domain, which is the implicit one where this installation is deployed: it
            // runs from the host's own output, with the example plugin already beside it.
            ["Morgana__Plugins__Directories__0"] = "plugins",

            // The same slot, read from the other side of the relationship: this installation admits
            // the caller instead of consulting it, only at the agent it actually publishes.
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__Name"] = FederatedCallerName,
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__SymmetricKey"] = FederationKey,
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__Enabled"] = "true",
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__OutboundPolicy__Enabled"] = "false",
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__InboundPolicy__Enabled"] = "true",
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__InboundPolicy__OnAgents__0"] = FederatedPeerAgent,
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__InboundPolicy__RateLimiting__Enabled"] = "true",
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__InboundPolicy__RateLimiting__MaxConversationsPerHour"] =
                ScopedPartnerConversationsPerHour.ToString(CultureInfo.InvariantCulture),

            // The two partners the instance under test declares for its own groups are admitted to
            // that same agent, which this installation does publish — they stay parked all the same,
            // since nothing on this run signs as either of them.
            [$"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__Enabled"] = "false",
            [$"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__Enabled"] = "false",

            // Where this installation says it is reached, rather than leaving its card to name what it
            // bound. The two agree here, so what is exercised is the declaration itself.
            ["Morgana__AgentToAgent__PublicUrl"] = FederatedPeerAddress
        };
}