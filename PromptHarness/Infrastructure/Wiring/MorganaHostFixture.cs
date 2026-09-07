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
/// Boots the real Morgana host in-process on an ephemeral Kestrel port and keeps it alive for the
/// whole test assembly.
/// </summary>
/// <remarks>
/// <para><strong>Why in-process and yet black-box.</strong> The suite talks to Morgana only over
/// HTTP — the same REST surface any channel uses, with the same JWT gate and the same webhook
/// delivery path — so nothing about the pipeline is mocked or shortcut. Hosting it inside the test
/// process buys two things a child process could not give: an <see cref="System.Diagnostics.ActivityListener"/>
/// that reads <c>morgana.agent</c> spans with no exporter or collector in the loop and a tee on
/// <c>Console.Out</c> that reads the tool log lines. Both are read-only observers.</para>
///
/// <para><strong>Configuration inheritance.</strong> The harness owns no <c>Morgana:</c>
/// configuration of its own. It resolves the very same stack <c>Morgana.Web</c> resolves
/// (<c>appsettings.json</c> + the shared user-secrets store, keyed by the <c>UserSecretsId</c> both
/// projects declare) and then republishes every resolved <c>Morgana:</c> key to the host as an
/// environment variable. Propagating explicitly — rather than trusting the host to re-resolve the
/// same secrets — keeps the suite correct under every runner, including those whose entry assembly
/// carries no <c>UserSecretsId</c> attribute of its own.</para>
///
/// <para><strong>What the harness overrides</strong> on top of that inherited configuration:
/// a throwaway SQLite storage path, telemetry exporters off (the in-process listener needs none),
/// rate and dust limiting off (they would throttle a repeated-run suite), the guard rail per
/// <c>Harness:EnableGuardrail</c> and a freshly-minted symmetric key for the <c>harness</c> issuer — so
/// the channel's credentials live for the duration of one run and never touch disk.</para>
/// </remarks>
public sealed class MorganaHostFixture : IAsyncLifetime
{
    /// <summary>Configuration resolved by the harness: the host's own appsettings plus the shared secrets store.</summary>
    public IConfiguration Configuration { get; private set; } = null!;

    /// <summary>Harness knobs from <c>appsettings.Harness.json</c>.</summary>
    public HarnessOptions Options { get; private set; } = null!;

    /// <summary>Base address of the host under test, e.g. <c>http://127.0.0.1:43117</c>.</summary>
    public string BaseAddress { get; private set; } = string.Empty;

    /// <summary>Symmetric key minted for this run and shared with the host as the <c>harness</c> issuer key.</summary>
    public string IssuerKey { get; private set; } = string.Empty;

    /// <summary>
    /// Partner this run declares, admitted to <see cref="ScopedPartnerAgent"/> and to no other desk,
    /// so the scope half of the A2A gate has something to actually refuse.
    /// </summary>
    /// <remarks>
    /// Declared per run rather than shipped: it exists to be turned away and a deployment carrying a
    /// partner nobody onboarded would be a worse default than the test is worth.
    /// </remarks>
    public const string ScopedPartnerName = "harness-peer";

    /// <summary>The one published agent <see cref="ScopedPartnerName"/> is admitted to.</summary>
    public const string ScopedPartnerAgent = "inventory";

    /// <summary>Symmetric key minted for this run under <see cref="ScopedPartnerName"/>.</summary>
    public string ScopedPartnerKey { get; private set; } = string.Empty;

    /// <summary>
    /// Conversations <see cref="ScopedPartnerName"/> may open in an hour. Declared because the
    /// instance under test demands a ceiling of every admitted partner, high enough never to be reached.
    /// </summary>
    public const int ScopedPartnerConversationsPerHour = 100_000;

    /// <summary>
    /// Position this run's partner takes in <c>Morgana:AgentToAgent:Partners</c>, past the last entry
    /// the host's own configuration declares. Read by the group that boots a doomed host to break one
    /// declaration at a time.
    /// </summary>
    public int ScopedPartnerIndex { get; private set; }

    /// <summary>
    /// A second partner, admitted to the same desk but allowed to open exactly one conversation an
    /// hour, so the ceiling can be observed doing something rather than merely being declared.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ScopedPartnerName"/> because the count is per issuer: a ceiling worth
    /// testing has to be met, and meeting it on the partner every other group calls would turn every
    /// later consultation into a refusal.
    /// </remarks>
    public const string MeteredPartnerName = "harness-peer-metered";

    /// <summary>Conversations <see cref="MeteredPartnerName"/> may open in an hour: the second is refused.</summary>
    public const int MeteredPartnerConversationsPerHour = 1;

    /// <summary>What <see cref="MeteredPartnerName"/> reads when it has opened its one conversation.</summary>
    /// <remarks>
    /// Written on that partner's own entry, so a turned-away colleague is answered in this
    /// deployment's voice rather than with a status code the asking model would narrate.
    /// </remarks>
    public const string MeteredPartnerRefusal = "The nursery cannot take on further exchanges with you this hour.";

    /// <summary>Symmetric key minted for this run under <see cref="MeteredPartnerName"/>.</summary>
    public string MeteredPartnerKey { get; private set; } = string.Empty;

    /// <summary>Position <see cref="MeteredPartnerName"/> takes in <c>Morgana:AgentToAgent:Partners</c>.</summary>
    public int MeteredPartnerIndex { get; private set; }

    /// <summary>
    /// The first position in <c>Morgana:AgentToAgent:Partners</c> no declaration occupies, which the
    /// group booting a doomed host writes its own broken entry into.
    /// </summary>
    public int FreePartnerIndex { get; private set; }

    /// <summary>
    /// The other installation of a federation run, as the instance under test knows it: the name its
    /// one agent consults a colleague at, written on the attribute in that plugin's code.
    /// </summary>
    public const string FederatedPeerName = "annex";

    /// <summary>The name that installation knows this one by, which its gate expects in the token.</summary>
    public const string FederatedCallerName = "front";

    /// <summary>The desk the other installation publishes and this one has no books for.</summary>
    public const string FederatedPeerAgent = "inventory";

    /// <summary>The colleague as it appears in the asking agent's own tool list.</summary>
    public const string FederatedPeerFunction = "consult_annex_inventory";

    /// <summary>The other installation of this run, or <c>null</c> when the run stands none up.</summary>
    public FederatedPeerHost? Peer { get; private set; }

    /// <summary>Where the other installation answers, claimed before either host starts.</summary>
    public string FederatedPeerAddress { get; private set; } = string.Empty;

    /// <summary>The one secret the two installations of a federation run share.</summary>
    public string FederationKey { get; private set; } = string.Empty;

    /// <summary>Port the other installation binds, reserved beside this instance's own.</summary>
    private int federatedPeerPort;

    /// <summary>Slot the federated relationship occupies, read from both ends of it.</summary>
    private int FederatedPartnerIndex { get; set; }

    /// <summary>
    /// Directory holding the per-conversation databases this run creates, read by the one assertion
    /// that asks which conversation a request was served on rather than what came back from it.
    /// </summary>
    public string StoragePath => storagePath;

    /// <summary>Tee on the host's stdout; the turn observer reads tool log lines from it.</summary>
    public HostOutputCapture Output { get; private set; } = null!;

    /// <summary>The harness's own channel, already handshaken and listening for callbacks.</summary>
    public HarnessChannel Channel { get; private set; } = null!;

    /// <summary>Reader of the per-turn structural signals.</summary>
    public TurnObserver Observer { get; private set; } = null!;

    /// <summary>Scenario engine bound to this host.</summary>
    public ScenarioRunner Runner { get; private set; } = null!;

    /// <summary>
    /// The judge <see cref="Runner"/> was built over, exposed for the one caller that judges a bare
    /// <see cref="Morgana.Contracts.ChannelMessage"/> outside any scripted turn (Presentation).
    /// </summary>
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

        // Step 2: mint per-run, disposable identity — a fresh JWT signing key (never written to
        // disk) and a scratch directory for the SQLite databases this run's conversations create.
        IssuerKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        ScopedPartnerKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        MeteredPartnerKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        storagePath = Path.Combine(Path.GetTempPath(), "morgana-harness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        // Step 3: claim the port the host will bind to. Reserved up front (not left to Kestrel's
        // own ":0" auto-pick) because the harness needs the number *before* starting the host, to
        // build its own channel and health-check URLs against it.
        int port = ReserveEphemeralPort();
        BaseAddress = $"http://127.0.0.1:{port}";

        // The other installation's port is claimed in the same breath, because the instance under
        // test has to be told where its partner answers before it starts and a partner declared
        // without an address is refused at boot.
        if (Options.FederatedPeer)
        {
            federatedPeerPort = ReserveEphemeralPort();
            FederatedPeerAddress = $"http://127.0.0.1:{federatedPeerPort}";
            FederationKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        }

        // Step 4: publish the resolved configuration (plus the harness's own overrides) as
        // environment variables, which is how the child host process picks it up.
        ApplyHostEnvironment();

        // Step 4b: on a federation run, the other installation goes up first. It inherits everything
        // just published — provider, keys, tiers — and is told only what makes it somebody else: its
        // own address and storage, the shipped domain instead of the one desk above and the partner
        // declaration admitting this instance to the desk it publishes.
        if (Options.FederatedPeer)
            Peer = await FederatedPeerHost.StartAsync(federatedPeerPort, BuildFederatedPeerEnvironment(), Options.StartupTimeoutSeconds);

        // The tee must be in place before the host constructs its logging stack, because the
        // console logger latches Console.Out once, when its provider is created.
        Output = HostOutputCapture.Install(Options.EchoHostOutput);

        // Step 5: launch the real Morgana.Web entry point on a background thread and block here
        // until it answers its health endpoint (or fails fast — see WaitForHealthyAsync).
        StartHost();

        await WaitForHealthyAsync();

        // Step 6: now that the host is up, wire the read-only observers (spans + log tee) and the
        // harness's own channel (a live webhook receiver + REST client) against it.
        Observer = new TurnObserver(Output, Options.LogDrainMilliseconds);

        Channel = new HarnessChannel(
            BaseAddress,
            IssuerKey,
            Configuration["Morgana:Authentication:Audience"] ?? "morgana.ai",
            drainTrailingSideMessages: Options.DustBudgetPerConversation is not null);
        await Channel.StartAsync();

        // Step 7: assemble the scenario engine last, since it depends on everything above —
        // the channel to drive conversations, the observer to read signals and a judge built
        // over the same LLM configuration the instance under test uses.
        judgeLoggerFactory = LoggerFactory.Create(logging => logging.ClearProviders());
        Judge = LLMJudge.Create(Configuration, judgeLoggerFactory);
        Runner = new ScenarioRunner(Channel, Observer, Judge, Options, DescribeLlm());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        // Tear down in reverse order of acquisition: stop accepting webhook callbacks first, then
        // release the span/log listeners, then the judge's (otherwise silent) logger factory.
        if (Channel is not null)
            await Channel.DisposeAsync();

        Observer?.Dispose();
        judgeLoggerFactory?.Dispose();

        // The other installation is a process of its own, so unlike the host under test it really can
        // be stopped from here — and has to be, along with the conversations it was asked to open.
        if (Peer is not null)
            await Peer.DisposeAsync();

        // The host runs on a background thread and stops with the test process; there is no
        // lifetime handle to signal from here. Only the throwaway databases are ours to clean up.
        try
        {
            if (Directory.Exists(storagePath))
                Directory.Delete(storagePath, recursive: true);
        }
        catch (IOException)
        {
            // A database file still held open by the host is not worth failing a run over.
        }
    }

    /// <summary>
    /// Renders the provider and the model bound to each tier, e.g.
    /// <c>Anthropic (Efficiency=claude-haiku-4-5, Performance=claude-sonnet-5)</c>. Recorded in
    /// every harness file: a token count is only comparable against the same models.
    /// </summary>
    private string DescribeLlm()
    {
        string provider = Configuration["Morgana:LLM:Provider"] ?? "(unknown)";

        // Walk every tier configured under the active provider (e.g. Efficiency, Performance) and
        // read the model id bound to each — the same shape ScenarioRunner reports per-scenario cost
        // against, so a harness row is always legible without cross-referencing appsettings.
        IEnumerable<string> tiers = Configuration.GetSection($"Morgana:LLM:{provider}:Tiers").GetChildren()
            .Select(tier => $"{tier.Key}={tier["Options:ModelId"] ?? "(unset)"}");

        return $"{provider} ({string.Join(", ", tiers)})";
    }

    /// <summary>
    /// Resolves the same configuration stack the host resolves: its <c>appsettings.json</c> (linked
    /// into this project's output), the harness knobs, the shared user-secrets store and the
    /// environment.
    /// </summary>
    private static IConfiguration BuildHarnessConfiguration()
        // Layered in precedence order, each provider overriding the previous: the host's own
        // appsettings.json (linked into this project's output by the .csproj), then the harness's
        // own knobs, then the shared user-secrets store (the actual provider/keys/tiers), then
        // whatever the environment already carries — so a CI runner's env vars still win last.
        => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Harness.json", optional: false)
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

    /// <summary>
    /// Publishes the resolved configuration to the host process-wide, then layers the harness
    /// overrides on top.
    /// </summary>
    private void ApplyHostEnvironment()
    {
        // Republish every resolved "Morgana:" key verbatim, translating IConfiguration's colon
        // section separator into the double-underscore form the environment-variable configuration
        // provider expects (ASP.NET Core's own convention). This is what makes the child host see
        // the exact same provider/keys/tiers the harness itself resolved, without re-reading the
        // secrets store a second time.
        foreach (KeyValuePair<string, string?> entry in Configuration.AsEnumerable())
        {
            if (entry.Value is null || !entry.Key.StartsWith("Morgana:", StringComparison.Ordinal))
                continue;

            Environment.SetEnvironmentVariable(entry.Key.Replace(":", "__"), entry.Value);
        }

        // From here on, each line is a harness-specific override layered on top of the inherited
        // configuration above — never something the instance under test would run with unattended.
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("Morgana__ConversationPersistence__StoragePath", storagePath);

        // The shipped domain, deployed beside this suite rather than in the directory scanned by
        // default: naming it here is what lets a run that swaps the domain leave it out, since an
        // installation reads the first agents.json it finds and holds exactly one domain.
        Environment.SetEnvironmentVariable("Morgana__Plugins__Directories__0", "domain-plugins");
        Environment.SetEnvironmentVariable("Morgana__ActorSystem__EnableGuardrail", Options.EnableGuardrail ? "true" : "false");
        Environment.SetEnvironmentVariable("Morgana__RateLimiting__Enabled", "false");

        // Unset by default (see HarnessOptions.DustBudgetPerConversation's own remarks): only
        // DustTests sets this, in its own filtered dotnet test invocation, so the rest of the suite
        // always runs against a budget no scripted few-turn conversation could ever dent.
        if (Options.DustBudgetPerConversation is { } dustBudget)
        {
            Environment.SetEnvironmentVariable("Morgana__DustLimiting__Enabled", "true");
            Environment.SetEnvironmentVariable("Morgana__DustLimiting__BudgetPerConversation", dustBudget.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            Environment.SetEnvironmentVariable("Morgana__DustLimiting__Enabled", "false");
        }

        // Examples' InventoryTool reads this to pin the order seal word to a known constant instead
        // of a fresh random one — see GenerateSealWord's and HarnessOptions.DeterministicSealWord's
        // own remarks. Without it, a scripted scenario could never recite the seal word back in its
        // second turn, since nothing in the harness DSL can capture a value the model invents
        // mid-run and reuse it in a later turn's fixed text.
        Environment.SetEnvironmentVariable("Harness__DeterministicSealWord", Options.DeterministicSealWord);

        // Unset by default (see HarnessOptions.SummarizationThreshold's own remarks): only
        // SummarizationTests sets these, in its own filtered dotnet test invocation, so the rest of
        // the suite always runs against the inherited, unmodified reducer configuration.
        if (Options.SummarizationThreshold is { } summarizationThreshold)
            Environment.SetEnvironmentVariable("Morgana__HistoryReducer__SummarizationThreshold", summarizationThreshold.ToString());
        if (Options.SummarizationTargetCount is { } summarizationTargetCount)
            Environment.SetEnvironmentVariable("Morgana__HistoryReducer__SummarizationTargetCount", summarizationTargetCount.ToString());

        // Telemetry stays on as an ActivitySource — the in-process listener is what reads it — but
        // no exporter is wanted: OTLP would spam a collector that may not be listening. The
        // exporter list is variable-length, so every configured entry is walked and switched off
        // by its own index rather than assuming how many there are.
        int exporterIndex = 0;
        foreach (IConfigurationSection _ in Configuration.GetSection("Morgana:OpenTelemetry:Exporters").GetChildren())
            Environment.SetEnvironmentVariable($"Morgana__OpenTelemetry__Exporters__{exporterIndex++}__Enabled", "false");

        // The freshly-minted per-run key replaces whatever sits in the shared secrets store, so this
        // run's credentials are never anything durable enough to leak. Only "harness" needs one: the
        // host signs the traffic between its own agents under a key it coins at startup, which is
        // configured nowhere and therefore cannot be overridden here.
        Environment.SetEnvironmentVariable($"Morgana__Authentication__Issuers__{ResolveIssuerIndex(HarnessChannel.IssuerName)}__SymmetricKey", IssuerKey);

        // One partner appended past the last index the host's own appsettings uses: admitted to a
        // single desk, which is the only way the scope half of the A2A gate can be observed at all —
        // refusing an unauthenticated call proves the door is shut, not that it is shut selectively.
        // Its key, its reach and its ceiling are one entry, so there is no second half to forget.
        ScopedPartnerIndex = Configuration.GetSection("Morgana:AgentToAgent:Partners").GetChildren().Count();
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__Name", ScopedPartnerName);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__SymmetricKey", ScopedPartnerKey);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__InboundPolicy__Enabled", "true");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__InboundPolicy__OnAgents__0", ScopedPartnerAgent);

        // How many conversations this partner may open in an hour, which the instance under test
        // demands of every admitted partner. Far above anything a scripted suite could reach: the
        // ceiling exists here to satisfy a declaration, never to be met.
        Environment.SetEnvironmentVariable(
            $"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__InboundPolicy__RateLimiting__Enabled", "true");
        Environment.SetEnvironmentVariable(
            $"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__InboundPolicy__RateLimiting__MaxConversationsPerHour",
            ScopedPartnerConversationsPerHour.ToString());

        // The same admission with a ceiling that can actually be met, and the sentence this deployment
        // turns a partner away with. One conversation an hour is the smallest bound that still lets the
        // first exchange be served, which is what makes the second one's refusal mean something.
        MeteredPartnerIndex = ScopedPartnerIndex + 1;
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__Name", MeteredPartnerName);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__SymmetricKey", MeteredPartnerKey);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__InboundPolicy__Enabled", "true");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__InboundPolicy__OnAgents__0", ScopedPartnerAgent);
        Environment.SetEnvironmentVariable(
            $"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__InboundPolicy__RateLimiting__Enabled", "true");
        Environment.SetEnvironmentVariable(
            $"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__InboundPolicy__RateLimiting__MaxConversationsPerHour",
            MeteredPartnerConversationsPerHour.ToString());
        Environment.SetEnvironmentVariable(
            $"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__InboundPolicy__RateLimiting__ErrorMessagePerHour",
            MeteredPartnerRefusal);

        // Where a broken declaration may be written without landing on either of the two above.
        FreePartnerIndex = MeteredPartnerIndex + 1;

        // On a federation run the instance under test knows one more partner — a whole other
        // installation — and its own domain is replaced by the single desk that holds a colleague
        // there. Off, none of this is written and the instance is the one every other group drives.
        if (Options.FederatedPeer)
            ApplyFederationEnvironment();


        // Framework categories at Information, everything else quiet: the tool log lines the turn
        // observer parses are Information-level and the rest is noise in the capture buffer.
        Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Morgana", Options.HostLogLevel);
    }

    /// <summary>
    /// Finds the position of a named entry in <c>Morgana:Authentication:Issuers</c>, whose key this
    /// run overrides.
    /// </summary>
    /// <param name="issuerName">Issuer whose index is wanted.</param>
    /// <exception cref="InvalidOperationException">Thrown when the host declares no such issuer.</exception>
    private int ResolveIssuerIndex(string issuerName)
    {
        // Issuers is a JSON array, so IConfiguration exposes each element as a child section whose
        // own Key is its array index as a string ("0", "1", ...) — that index is exactly the
        // fragment ApplyHostEnvironment needs to target this one entry's SymmetricKey.
        foreach (IConfigurationSection issuer in Configuration.GetSection("Morgana:Authentication:Issuers").GetChildren())
        {
            if (string.Equals(issuer["Name"], issuerName, StringComparison.OrdinalIgnoreCase))
                return int.Parse(issuer.Key);
        }

        // Fail fast, before the host even starts: proceeding would let the fixture boot a host
        // that can never authenticate the harness's own channel, turning a config gap into a
        // confusing timeout many steps later instead of a clear error now.
        throw new InvalidOperationException(
            $"Morgana:Authentication:Issuers contains no '{issuerName}' entry. The harness mints a per-run key for it and " +
            "cannot run against an instance that does not declare it.");
    }

    /// <summary>
    /// Asks the OS for a free TCP port by binding and immediately releasing it. Kestrel could pick
    /// its own with <c>:0</c>, but the harness needs the number up front to address the host.
    /// </summary>
    private static int ReserveEphemeralPort()
    {
        // Binding to port 0 asks the OS kernel to allocate the next free ephemeral port; reading it
        // back from LocalEndpoint and immediately stopping the listener frees it again for Kestrel
        // to bind moments later — a brief, practically-safe race, not a true reservation.
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    /// <summary>
    /// Invokes <c>Morgana.Web</c>'s entry point on a background thread. The content root is this
    /// project's output directory, which holds both the linked <c>appsettings.json</c> and the
    /// <c>plugins/</c> folder the plugin loader scans.
    /// </summary>
    private void StartHost()
    {
        // typeof(Program) resolves against whichever assembly the compiler sees a global-namespace
        // "Program" type in first; the guard right below exists precisely because that resolution
        // is implicit, not because this line itself can fail.
        Assembly hostAssembly = typeof(Program).Assembly;

        // Program is a global-namespace type, so a same-named type appearing in this assembly would
        // silently win the lookup and we would end up re-entering the test runner's own entry point.
        if (hostAssembly.GetName().Name != "Morgana.Web")
            throw new InvalidOperationException(
                $"Expected the global Program type to come from Morgana.Web, but it came from {hostAssembly.GetName().Name}.");

        MethodInfo entryPoint = hostAssembly.EntryPoint
            ?? throw new InvalidOperationException("Morgana.Web exposes no entry point.");

        // Reconstruct the same command-line arguments `dotnet run` would pass, since the entry
        // point is being invoked directly rather than through a process boundary that would
        // otherwise supply them.
        string[] arguments =
        [
            "--urls", BaseAddress,
            "--environment", "Development",
            "--contentRoot", AppContext.BaseDirectory,

            // Load-bearing. ApplicationName defaults to the entry assembly, which under a test
            // runner is the runner itself — and MVC discovers controllers from the application
            // part named by it. Without this the host starts, serves and answers 404 to every
            // route, because it never found a controller.
            "--applicationName", hostAssembly.GetName().Name!
        ];

        // The host must run on its own thread: InitializeAsync needs to return control so the test
        // process can poll the health endpoint and top-level Main here would otherwise block
        // until Kestrel shuts down (i.e. never, until the whole process exits).
        Thread hostThread = new Thread(() =>
        {
            try
            {
                // Top-level statements with a top-level await compile to an async entry point, so
                // Invoke hands back a Task: awaiting it is the only way a startup failure surfaces
                // here instead of vanishing on a thread nobody joins.
                if (entryPoint.Invoke(null, [arguments]) is Task hostTask)
                    hostTask.GetAwaiter().GetResult();
            }
            catch (TargetInvocationException ex)
            {
                // Reflection wraps whatever the invoked method threw; unwrap it so
                // WaitForHealthyAsync reports the host's real startup error, not a generic
                // "invocation failed" wrapper with the useful part buried in InnerException.
                hostFailure = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                hostFailure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "morgana-harness-host"
        };

        hostThread.Start();
    }

    /// <summary>
    /// Polls the unauthenticated health endpoint until the host answers or the startup budget runs out.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the host failed to start, with its own startup error as the inner exception.</exception>
    private async Task WaitForHealthyAsync()
    {
        using HttpClient httpClient = new HttpClient();
        httpClient.BaseAddress = new Uri(BaseAddress);
        httpClient.Timeout = TimeSpan.FromSeconds(5);

        string lastProbe = "never answered";

        // Poll on a fixed cadence until either the host answers or the configured startup budget
        // runs out — Kestrel's own startup (plugin scan, agent registry, LLM tier validation) has
        // no synchronous "ready" signal this process can await directly.
        DateTime deadline = DateTime.UtcNow.AddSeconds(Options.StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            // Checked every iteration, not just once: the host can crash after already having
            // answered a transient probe failure and a fail-fast startup error is worth surfacing
            // immediately rather than waiting out the rest of the timeout for nothing.
            if (hostFailure is not null)
                throw new InvalidOperationException(
                    "Morgana failed to start. Startup validation is fail-fast: check the LLM provider, its Tiers map and " +
                    $"the agent/intent registry against the shared user-secrets store.{FormatHostOutput()}", hostFailure);

            try
            {
                using HttpResponseMessage response = await httpClient.GetAsync("/api/morgana/health");
                if (response.IsSuccessStatusCode)
                    return;

                // Not a success but not an exception either — the host is up and answering, just
                // not yet healthy (e.g. still validating startup). Keep the response body around so
                // a timeout, if it comes to that, shows the last thing the host actually said.
                lastProbe = $"HTTP {(int)response.StatusCode} — {await response.Content.ReadAsStringAsync()}";
            }
            catch (HttpRequestException ex)
            {
                // Expected during the window before Kestrel has bound the port at all — connection
                // refused, not a real failure yet.
                lastProbe = $"{ex.GetType().Name}: {ex.Message}";
            }
            catch (TaskCanceledException)
            {
                lastProbe = "request timed out";
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Morgana did not become healthy within {Options.StartupTimeoutSeconds}s at {BaseAddress}. " +
            $"Last probe: {lastProbe}.{FormatHostOutput()}");
    }

    /// <summary>
    /// Tail of the captured host output, appended to startup failures. Without it the host's own
    /// diagnosis is invisible: the tee swallows stdout by design, so a startup error would otherwise
    /// surface as a bare timeout.
    /// </summary>
    private string FormatHostOutput()
    {
        // Since(0) reads the tee's entire buffer from the start of this run — not a per-turn
        // window like the observer uses — because a startup failure has no turn to scope it to.
        IReadOnlyList<string> lines = Output.Since(0);
        if (lines.Count == 0)
            return "\n\nThe host produced no output at all — its entry point may never have run.";

        // Capped at the last 60 lines: enough to see the actual startup error without dumping an
        // entire noisy boot sequence into every timeout message.
        return $"\n\n--- host output (last {Math.Min(lines.Count, 60)} of {lines.Count} lines) ---\n"
             + string.Join("\n", lines.TakeLast(60));
    }

    /// <summary>
    /// Tells the instance under test that it holds one desk of its own and one colleague abroad.
    /// </summary>
    /// <remarks>
    /// The domain is <em>replaced</em> rather than added to: loading the shipped one beside it would
    /// give this instance a greenhouse of its own, and a desk that can answer from its own books
    /// proves nothing about a colleague across the wire. What is left is one agent whose only
    /// competence is having that colleague.
    /// </remarks>
    private void ApplyFederationEnvironment()
    {
        // The one domain this instance holds on this run, in place of the shipped one. Replaced and
        // not added to: an installation reads one agents.json, so two deployed domains would race
        // and the desk declaring a colleague abroad would be the one whose intents went missing.
        Environment.SetEnvironmentVariable("Morgana__Plugins__Directories__0", "federation-plugins");

        // The partner the plugin's own attribute names. Its address is where the other installation
        // was told to answer and the issuer is the name that installation filed this one under —
        // which no card can carry, being the name of one caller among however many it has.
        FederatedPartnerIndex = FreePartnerIndex;
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__Name", FederatedPeerName);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__Url", FederatedPeerAddress);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__SymmetricKey", FederationKey);
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__Enabled", "true");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__OutboundPolicy__Enabled", "true");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__OutboundPolicy__Issuer", FederatedCallerName);

        // The two partners declared above this one are admitted to a desk the shipped domain
        // publishes and this domain does not, which startup refuses. They belong to the groups that
        // drive the instance as itself, so on this run they are parked rather than corrected.
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__Enabled", "false");
        Environment.SetEnvironmentVariable($"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__Enabled", "false");

        // Past the entry just written, so a doomed-boot case still lands on a slot nobody declared.
        FreePartnerIndex = FederatedPartnerIndex + 1;
    }

    /// <summary>
    /// Tells the other installation who it is: the shipped domain, and one partner admitted to the
    /// desk this instance has no books for.
    /// </summary>
    /// <remarks>
    /// Everything else it needs — provider, tiers, keys, the switches this suite turns off — it
    /// inherits from the environment published a moment ago, which is what keeps the two
    /// installations two configurations of one deployment rather than two deployments.
    /// </remarks>
    private Dictionary<string, string> BuildFederatedPeerEnvironment()
        => new Dictionary<string, string>
        {
            // The shipped domain, which is the implicit one where this installation is deployed: it
            // runs from the host's own output, with the example plugin already beside it.
            ["Morgana__Plugins__Directories__0"] = "plugins",

            // The same slot, read from the other side of the relationship: this installation admits
            // the caller instead of consulting it, and only at the desk it actually publishes.
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__Name"] = FederatedCallerName,
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__SymmetricKey"] = FederationKey,
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__Enabled"] = "true",
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__OutboundPolicy__Enabled"] = "false",
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__InboundPolicy__Enabled"] = "true",
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__InboundPolicy__OnAgents__0"] = FederatedPeerAgent,
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__InboundPolicy__RateLimiting__Enabled"] = "true",
            [$"Morgana__AgentToAgent__Partners__{FederatedPartnerIndex}__InboundPolicy__RateLimiting__MaxConversationsPerHour"] =
                ScopedPartnerConversationsPerHour.ToString(),

            // The two partners the instance under test declares for its own groups are admitted to
            // that same desk, which this installation does publish — they stay parked all the same,
            // since nothing on this run signs as either of them.
            [$"Morgana__AgentToAgent__Partners__{ScopedPartnerIndex}__Enabled"] = "false",
            [$"Morgana__AgentToAgent__Partners__{MeteredPartnerIndex}__Enabled"] = "false",

            // Where this installation says it is reached, rather than leaving its card to name what it
            // bound. The two agree here, so what is exercised is the declaration itself.
            ["Morgana__AgentToAgent__PublicUrl"] = FederatedPeerAddress
        };
}
