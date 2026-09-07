using System.Diagnostics;
using System.Reflection;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>
/// The second Morgana of a federation run: another installation, in another process, publishing the
/// desk the instance under test consults across the wire.
/// </summary>
/// <remarks>
/// <para>A process rather than a second host beside the first, because configuration reaches a
/// Morgana through the environment and an environment belongs to a process: two installations in one
/// could not disagree about who their partners are, which is the whole of what this one is here to
/// disagree about.</para>
///
/// <para>It serves the example domain — the greenhouse the asking side has no books for — and is
/// started from Morgana.Web's own output, where that plugin is already deployed beside the entry
/// point. Nothing about it is special-cased: it is the shipped host, told who its partner is.</para>
/// </remarks>
public sealed class FederatedPeerHost : IAsyncDisposable
{
    /// <summary>Where this installation answers, as the instance under test will be told to reach it.</summary>
    public string BaseAddress { get; }

    /// <summary>The running installation, stopped when the run ends.</summary>
    private readonly Process process;

    /// <summary>Throwaway directory holding the conversations this installation is asked to open.</summary>
    private readonly string storagePath;

    /// <summary>Starts the second installation and holds what is needed to stop it again.</summary>
    private FederatedPeerHost(Process process, string baseAddress, string storagePath)
    {
        this.process = process;
        this.storagePath = storagePath;
        BaseAddress = baseAddress;
    }

    /// <summary>
    /// Launches the second installation and waits until it answers, so a consultation never lands on
    /// a host that is still validating its own startup.
    /// </summary>
    /// <param name="port">Port it binds, already reserved by the caller.</param>
    /// <param name="environment">What this installation is told about itself, layered over what it inherits.</param>
    /// <param name="startupTimeoutSeconds">How long it is given to answer before the run gives up on it.</param>
    public static async Task<FederatedPeerHost> StartAsync(
        int port,
        IReadOnlyDictionary<string, string> environment,
        int startupTimeoutSeconds)
    {
        string baseAddress = $"http://127.0.0.1:{port}";
        string storagePath = Path.Combine(Path.GetTempPath(), "morgana-harness-peer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        ProcessStartInfo startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,

            // Its output is swallowed: the run reads what this installation did from what the
            // instance under test answered, never from what the peer said about itself.
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add(ResolveEntryPoint());

        // Everything the parent resolved is inherited — provider, keys, tiers — and only what makes
        // this a second installation is written over it.
        foreach ((string key, string value) in environment)
            startInfo.Environment[key] = value;

        startInfo.Environment["ASPNETCORE_URLS"] = baseAddress;
        startInfo.Environment["Morgana__ConversationPersistence__StoragePath"] = storagePath;

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The second installation of the federation run could not be started.");

        // Read to the end and discarded: a child whose output nobody drains blocks once its pipe fills.
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        await WaitForHealthyAsync(process, baseAddress, startupTimeoutSeconds);

        return new FederatedPeerHost(process, baseAddress, storagePath);
    }

    /// <summary>Where the shipped host is deployed, recorded at build time by the harness project.</summary>
    /// <remarks>
    /// A child process needs the runtime configuration and dependency manifest generated in that
    /// output, neither of which travels into this one — and the example plugin is already deployed
    /// there, which is the domain this installation has to serve.
    /// </remarks>
    private static string ResolveEntryPoint()
    {
        string? entryPoint = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(metadata => metadata.Key == "MorganaWebEntryPoint")?.Value;

        if (entryPoint is null || !File.Exists(entryPoint))
        {
            throw new InvalidOperationException(
                $"The second installation cannot be started: no host was found at '{entryPoint ?? "(unrecorded)"}'. "
                + "Build Morgana.Web in the same configuration as this suite.");
        }

        return entryPoint;
    }

    /// <summary>Polls until the second installation answers, or gives up on it.</summary>
    /// <param name="process">The installation being waited for; one that has already exited is not waited out.</param>
    /// <param name="baseAddress">Where it was told to answer.</param>
    /// <param name="startupTimeoutSeconds">How long it is given.</param>
    private static async Task WaitForHealthyAsync(Process process, string baseAddress, int startupTimeoutSeconds)
    {
        using HttpClient httpClient = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(5) };

        DateTime deadline = DateTime.UtcNow.AddSeconds(startupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            // Startup validation is fail-fast, so an installation that refused its own configuration
            // is already gone and nothing is gained by waiting out the rest of the budget.
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The second installation exited with code {process.ExitCode} before answering. Its trust configuration "
                    + "is refused at boot the way this instance's own is.");
            }

            try
            {
                using HttpResponseMessage response = await httpClient.GetAsync("/api/morgana/health");
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (HttpRequestException)
            {
                // The window before it has bound its port at all.
            }
            catch (TaskCanceledException)
            {
                // A probe that outlived its own timeout; the next one decides.
            }

            await Task.Delay(250);
        }

        throw new TimeoutException($"The second installation did not answer at {baseAddress} within {startupTimeoutSeconds}s.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone, which is the state this was after anyway.
        }

        process.Dispose();

        try
        {
            if (Directory.Exists(storagePath))
                Directory.Delete(storagePath, recursive: true);
        }
        catch (IOException)
        {
            // A database file still held open is not worth failing a run over.
        }
    }
}
