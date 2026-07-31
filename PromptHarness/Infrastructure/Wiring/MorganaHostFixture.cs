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
/// that reads <c>morgana.agent</c> spans with no exporter or collector in the loop, and a tee on
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
/// <c>Harness:EnableGuardrail</c>, and a freshly-minted symmetric key for the <c>harness</c> issuer — so
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
        storagePath = Path.Combine(Path.GetTempPath(), "morgana-harness", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        // Step 3: claim the port the host will bind to. Reserved up front (not left to Kestrel's
        // own ":0" auto-pick) because the harness needs the number *before* starting the host, to
        // build its own channel and health-check URLs against it.
        int port = ReserveEphemeralPort();
        BaseAddress = $"http://127.0.0.1:{port}";

        // Step 4: publish the resolved configuration (plus the harness's own overrides) as
        // environment variables, which is how the child host process picks it up.
        ApplyHostEnvironment();

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
            Configuration["Morgana:Authentication:Audience"] ?? "morgana.ai");
        await Channel.StartAsync();

        // Step 7: assemble the scenario engine last, since it depends on everything above —
        // the channel to drive conversations, the observer to read signals, and a judge built
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
        Environment.SetEnvironmentVariable("Morgana__ActorSystem__EnableGuardrail", Options.EnableGuardrail ? "true" : "false");
        Environment.SetEnvironmentVariable("Morgana__RateLimiting__Enabled", "false");
        Environment.SetEnvironmentVariable("Morgana__DustLimiting__Enabled", "false");

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

        // The freshly-minted per-run key replaces whatever "harness" issuer key sits in the shared
        // secrets store, so this run's credentials are never anything durable enough to leak.
        Environment.SetEnvironmentVariable($"Morgana__Authentication__Issuers__{ResolveHarnessIssuerIndex()}__SymmetricKey", IssuerKey);

        // Framework categories at Information, everything else quiet: the tool log lines the turn
        // observer parses are Information-level, and the rest is noise in the capture buffer.
        Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Morgana", Options.HostLogLevel);
    }

    /// <summary>
    /// Finds the position of the <c>harness</c> entry in <c>Morgana:Authentication:Issuers</c>, whose
    /// key this run overrides.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the host declares no <c>harness</c> issuer.</exception>
    private int ResolveHarnessIssuerIndex()
    {
        // Issuers is a JSON array, so IConfiguration exposes each element as a child section whose
        // own Key is its array index as a string ("0", "1", ...) — that index is exactly the
        // fragment ApplyHostEnvironment needs to target this one entry's SymmetricKey.
        foreach (IConfigurationSection issuer in Configuration.GetSection("Morgana:Authentication:Issuers").GetChildren())
        {
            if (string.Equals(issuer["Name"], HarnessChannel.IssuerName, StringComparison.OrdinalIgnoreCase))
                return int.Parse(issuer.Key);
        }

        // Fail fast, before the host even starts: proceeding would let the fixture boot a host
        // that can never authenticate the harness's own channel, turning a config gap into a
        // confusing timeout many steps later instead of a clear error now.
        throw new InvalidOperationException(
            $"Morgana:Authentication:Issuers contains no '{HarnessChannel.IssuerName}' entry. The harness authenticates as its own " +
            "channel and cannot run against an instance that does not declare it.");
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
            // part named by it. Without this the host starts, serves, and answers 404 to every
            // route, because it never found a controller.
            "--applicationName", hostAssembly.GetName().Name!
        ];

        // The host must run on its own thread: InitializeAsync needs to return control so the test
        // process can poll the health endpoint, and top-level Main here would otherwise block
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
            // answered a transient probe failure, and a fail-fast startup error is worth surfacing
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
}
