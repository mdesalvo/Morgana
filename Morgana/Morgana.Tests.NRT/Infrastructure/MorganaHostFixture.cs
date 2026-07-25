using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.Tests.NRT.Scenarios;
using Xunit;

namespace Morgana.Tests.NRT.Infrastructure;

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
/// <c>Nrt:EnableGuardrail</c>, and a freshly-minted symmetric key for the <c>nrt</c> issuer — so
/// the channel's credentials live for the duration of one run and never touch disk.</para>
/// </remarks>
public sealed class MorganaHostFixture : IAsyncLifetime
{
    /// <summary>Configuration resolved by the harness: the host's own appsettings plus the shared secrets store.</summary>
    public IConfiguration Configuration { get; private set; } = null!;

    /// <summary>Harness knobs from <c>appsettings.NRT.json</c>.</summary>
    public NrtOptions Options { get; private set; } = null!;

    /// <summary>Base address of the host under test, e.g. <c>http://127.0.0.1:43117</c>.</summary>
    public string BaseAddress { get; private set; } = string.Empty;

    /// <summary>Symmetric key minted for this run and shared with the host as the <c>nrt</c> issuer key.</summary>
    public string IssuerKey { get; private set; } = string.Empty;

    /// <summary>Tee on the host's stdout; the turn observer reads tool log lines from it.</summary>
    public HostOutputCapture Output { get; private set; } = null!;

    /// <summary>The harness's own channel, already handshaken and listening for callbacks.</summary>
    public NrtChannel Channel { get; private set; } = null!;

    /// <summary>Reader of the per-turn structural signals.</summary>
    public TurnObserver Observer { get; private set; } = null!;

    /// <summary>Scenario engine bound to this host.</summary>
    public ScenarioRunner Runner { get; private set; } = null!;

    /// <summary>Logger factory handed to the judge's LLM client; deliberately silent.</summary>
    private ILoggerFactory? judgeLoggerFactory;

    /// <summary>Directory holding the per-conversation SQLite databases created during the run.</summary>
    private string storagePath = string.Empty;

    /// <summary>Fatal exception thrown by the host's entry point, republished to the first test that observes it.</summary>
    private Exception? hostFailure;

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        Configuration = BuildHarnessConfiguration();
        Options = NrtOptions.Load(Configuration);

        IssuerKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        storagePath = Path.Combine(Path.GetTempPath(), "morgana-nrt", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        int port = ReserveEphemeralPort();
        BaseAddress = $"http://127.0.0.1:{port}";

        ApplyHostEnvironment();

        // The tee must be in place before the host constructs its logging stack, because the
        // console logger latches Console.Out once, when its provider is created.
        Output = HostOutputCapture.Install(Options.EchoHostOutput);

        StartHost();

        await WaitForHealthyAsync();

        Observer = new TurnObserver(Output, Options.LogDrainMilliseconds);

        Channel = new NrtChannel(
            BaseAddress,
            IssuerKey,
            Configuration["Morgana:Authentication:Audience"] ?? "morgana.ai");
        await Channel.StartAsync();

        judgeLoggerFactory = LoggerFactory.Create(logging => logging.ClearProviders());
        Runner = new ScenarioRunner(Channel, Observer, LlmJudge.Create(Configuration, judgeLoggerFactory), Options);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
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
    /// Resolves the same configuration stack the host resolves: its <c>appsettings.json</c> (linked
    /// into this project's output), the harness knobs, the shared user-secrets store and the
    /// environment.
    /// </summary>
    private static IConfiguration BuildHarnessConfiguration()
        => new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.NRT.json", optional: false)
            .AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables()
            .Build();

    /// <summary>
    /// Publishes the resolved configuration to the host process-wide, then layers the harness
    /// overrides on top.
    /// </summary>
    private void ApplyHostEnvironment()
    {
        foreach (KeyValuePair<string, string?> entry in Configuration.AsEnumerable())
        {
            if (entry.Value is null || !entry.Key.StartsWith("Morgana:", StringComparison.Ordinal))
                continue;

            Environment.SetEnvironmentVariable(entry.Key.Replace(":", "__"), entry.Value);
        }

        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
        Environment.SetEnvironmentVariable("Morgana__ConversationPersistence__StoragePath", storagePath);
        Environment.SetEnvironmentVariable("Morgana__ActorSystem__EnableGuardrail", Options.EnableGuardrail ? "true" : "false");
        Environment.SetEnvironmentVariable("Morgana__RateLimiting__Enabled", "false");
        Environment.SetEnvironmentVariable("Morgana__DustLimiting__Enabled", "false");

        // Telemetry stays on as an ActivitySource — the in-process listener is what reads it — but
        // no exporter is wanted: OTLP would spam a collector that may not be listening.
        int exporterIndex = 0;
        foreach (IConfigurationSection _ in Configuration.GetSection("Morgana:OpenTelemetry:Exporters").GetChildren())
            Environment.SetEnvironmentVariable($"Morgana__OpenTelemetry__Exporters__{exporterIndex++}__Enabled", "false");

        Environment.SetEnvironmentVariable($"Morgana__Authentication__Issuers__{ResolveNrtIssuerIndex()}__SymmetricKey", IssuerKey);

        // Framework categories at Information, everything else quiet: the tool log lines the turn
        // observer parses are Information-level, and the rest is noise in the capture buffer.
        Environment.SetEnvironmentVariable("Logging__LogLevel__Default", "Warning");
        Environment.SetEnvironmentVariable("Logging__LogLevel__Morgana", Options.HostLogLevel);
    }

    /// <summary>
    /// Finds the position of the <c>nrt</c> entry in <c>Morgana:Authentication:Issuers</c>, whose
    /// key this run overrides.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the host declares no <c>nrt</c> issuer.</exception>
    private int ResolveNrtIssuerIndex()
    {
        foreach (IConfigurationSection issuer in Configuration.GetSection("Morgana:Authentication:Issuers").GetChildren())
        {
            if (string.Equals(issuer["Name"], NrtChannel.IssuerName, StringComparison.OrdinalIgnoreCase))
                return int.Parse(issuer.Key);
        }

        throw new InvalidOperationException(
            $"Morgana:Authentication:Issuers contains no '{NrtChannel.IssuerName}' entry. The harness authenticates as its own " +
            "channel and cannot run against an instance that does not declare it.");
    }

    /// <summary>
    /// Asks the OS for a free TCP port by binding and immediately releasing it. Kestrel could pick
    /// its own with <c>:0</c>, but the harness needs the number up front to address the host.
    /// </summary>
    private static int ReserveEphemeralPort()
    {
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
        Assembly hostAssembly = typeof(Program).Assembly;

        // Program is a global-namespace type, so a same-named type appearing in this assembly would
        // silently win the lookup and we would end up re-entering the test runner's own entry point.
        if (hostAssembly.GetName().Name != "Morgana.Web")
            throw new InvalidOperationException(
                $"Expected the global Program type to come from Morgana.Web, but it came from {hostAssembly.GetName().Name}.");

        MethodInfo entryPoint = hostAssembly.EntryPoint
            ?? throw new InvalidOperationException("Morgana.Web exposes no entry point.");

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
                hostFailure = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                hostFailure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "morgana-nrt-host"
        };

        hostThread.Start();
    }

    /// <summary>
    /// Polls the unauthenticated health endpoint until the host answers or the startup budget runs out.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the host failed to start, with its own startup error as the inner exception.</exception>
    private async Task WaitForHealthyAsync()
    {
        using HttpClient httpClient = new HttpClient { BaseAddress = new Uri(BaseAddress), Timeout = TimeSpan.FromSeconds(5) };

        string lastProbe = "never answered";

        DateTime deadline = DateTime.UtcNow.AddSeconds(Options.StartupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (hostFailure is not null)
                throw new InvalidOperationException(
                    "Morgana failed to start. Startup validation is fail-fast: check the LLM provider, its Tiers map and " +
                    $"the agent/intent registry against the shared user-secrets store.{FormatHostOutput()}", hostFailure);

            try
            {
                using HttpResponseMessage response = await httpClient.GetAsync("/api/morgana/health");
                if (response.IsSuccessStatusCode)
                    return;

                lastProbe = $"HTTP {(int)response.StatusCode} — {await response.Content.ReadAsStringAsync()}";
            }
            catch (HttpRequestException ex)
            {
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
        IReadOnlyList<string> lines = Output.Since(0);
        if (lines.Count == 0)
            return "\n\nThe host produced no output at all — its entry point may never have run.";

        return $"\n\n--- host output (last {Math.Min(lines.Count, 60)} of {lines.Count} lines) ---\n"
             + string.Join("\n", lines.TakeLast(60));
    }
}
