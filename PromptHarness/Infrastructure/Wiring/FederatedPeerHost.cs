using System.Diagnostics;
using System.Reflection;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>
/// The second Morgana of a federation run: another installation in another process, publishing the agent the instance under test consults across the wire.
/// Configuration reaches a Morgana through its process's environment, so two installations in one process could not disagree about their partners.
/// It is the shipped host started from Morgana.Web's own output, where the example domain is deployed, told who its partner is.
/// </summary>
public sealed class FederatedPeerHost : IAsyncDisposable
{
    /// <summary>Where this installation answers, as the instance under test will be told to reach it.</summary>
    public string BaseAddress { get; }

    /// <summary>The running installation, stopped when the run ends.</summary>
    private readonly Process process;

    /// <summary>Throwaway directory holding the conversations this installation is asked to open.</summary>
    private readonly string storagePath;

    /// <summary>Holds the running installation and its storage, so both can be stopped and removed again.</summary>
    private FederatedPeerHost(Process process, string baseAddress, string storagePath)
    {
        this.process = process;
        this.storagePath = storagePath;
        BaseAddress = baseAddress;
    }

    /// <summary>Launches the second installation and waits until it answers, so a consultation never lands on a host still validating its startup.</summary>
    /// <param name="port">Port it binds, already reserved by the caller.</param>
    /// <param name="environment">What this installation is told about itself, layered over what it inherits.</param>
    /// <param name="startupTimeoutSeconds">How long it is given to answer before the run gives up on it.</param>
    public static async Task<FederatedPeerHost> StartAsync(
        int port,
        IReadOnlyDictionary<string, string> environment,
        int startupTimeoutSeconds)
    {
        // Loopback on the reserved port, the address the instance under test is told to consult.
        string baseAddress = $"http://127.0.0.1:{port}";

        // Storage of its own, so the conversations it is asked to open never mix with the instance under test's.
        string storagePath = Path.Combine(Path.GetTempPath(), "morgana-harness-peer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storagePath);

        // The shipped host under dotnet, as a deployment runs it.
        ProcessStartInfo startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,

            // Its output is swallowed: the run reads what this installation did from what the
            // instance under test answered, never from what the peer said about itself.
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add(ResolveEntryPoint());

        // Everything the parent resolved is inherited (provider, keys, tiers) while only what makes
        // this a second installation is written over it.
        foreach ((string key, string value) in environment)
            startInfo.Environment[key] = value;

        // Its address and storage are the two things no inherited setting may decide.
        startInfo.Environment["ASPNETCORE_URLS"] = baseAddress;
        startInfo.Environment["Morgana__ConversationPersistence__StoragePath"] = storagePath;

        // The installation that answers the consultations for the whole run.
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The second installation of the federation run could not be started.");

        // Read to the end and discarded: a child whose output nobody drains blocks once its pipe fills.
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        // A consultation sent before the host answers would read as a federation failure.
        await WaitForHealthyAsync(process, baseAddress, startupTimeoutSeconds);

        // The running installation, which the fixture stops at the end of the run.
        return new FederatedPeerHost(process, baseAddress, storagePath);
    }

    /// <summary>
    /// Where the shipped host is deployed, recorded at build time by the harness project.
    /// A child process needs the runtime configuration and dependency manifest of that output, where the example plugin it serves is also deployed.
    /// </summary>
    private static string ResolveEntryPoint()
    {
        // The path the harness project wrote into this assembly when it built.
        string? entryPoint = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(metadata => metadata.Key == "MorganaWebEntryPoint")?.Value;

        // A host that was not built cannot be started, so the run stops here with the path that was looked at.
        if (entryPoint is null || !File.Exists(entryPoint))
        {
            throw new InvalidOperationException(
                $"The second installation cannot be started: no host was found at '{entryPoint ?? "(unrecorded)"}'. "
                + "Build Morgana.Web in the same configuration as this suite.");
        }

        // The host's own dll, which dotnet runs.
        return entryPoint;
    }

    /// <summary>Polls until the second installation answers, giving up on it once the budget is spent.</summary>
    /// <param name="process">The installation being waited for; one that has already exited is not waited out.</param>
    /// <param name="baseAddress">Where it was told to answer.</param>
    /// <param name="startupTimeoutSeconds">How long it is given.</param>
    private static async Task WaitForHealthyAsync(Process process, string baseAddress, int startupTimeoutSeconds)
    {
        // The probe asks the health endpoint, which answers once the boot's checks have passed.
        using HttpClient httpClient = new HttpClient { BaseAddress = new Uri(baseAddress), Timeout = TimeSpan.FromSeconds(5) };

        // The installation is waited for until the run's budget for its startup is spent.
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
                // A healthy answer means the boot passed its checks and the actor system runs.
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

            // The next probe follows after a short pause.
            await Task.Delay(250);
        }

        // The budget is spent and nothing answered.
        throw new TimeoutException($"The second installation did not answer at {baseAddress} within {startupTimeoutSeconds}s.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            // A running installation holds its port, so it is stopped with everything it started.
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

        // The handle is released once the process is gone.
        process.Dispose();

        try
        {
            // The conversations it opened are throwaway, like the run's own storage.
            if (Directory.Exists(storagePath))
                Directory.Delete(storagePath, recursive: true);
        }
        catch (IOException)
        {
            // A database file still held open is not worth failing a run over.
        }
    }
}