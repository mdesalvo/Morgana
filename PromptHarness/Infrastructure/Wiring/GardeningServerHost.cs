using System.Diagnostics;
using System.Reflection;

namespace PromptHarness.Infrastructure.Wiring;

/// <summary>The GardeningMCP server of a run: the process that the shipped GardeningAgent reads its tools from.</summary>
public sealed class GardeningServerHost : IAsyncDisposable
{
    /// <summary>Where the shipped GardeningAgent looks for its server, spelled out as every published literal of the harness is.</summary>
    public const string Address = "http://localhost:5006/mcp";

    /// <summary>The running server, stopped when the run ends.</summary>
    private readonly Process process;

    /// <summary>Holds the running server so that it can be stopped again.</summary>
    private GardeningServerHost(Process process) => this.process = process;

    /// <summary>Launches the server and waits until it answers, so the instance under test never examines an offer that is not there yet.</summary>
    /// <param name="startupTimeoutSeconds">How long the server is given to answer before the run gives up on it.</param>
    public static async Task<GardeningServerHost> StartAsync(int startupTimeoutSeconds)
    {
        // The output of the server, recorded at build time by the harness project.
        string? entryPoint = Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(metadata => metadata.Key == "GardeningServerEntryPoint")?.Value;

        // A server that was not built cannot be started so the run is stopped here with the path that was looked at.
        if (entryPoint is null || !File.Exists(entryPoint))
        {
            throw new InvalidOperationException(
                $"The gardening server cannot be started: no server was found at '{entryPoint ?? "(unrecorded)"}'. "
                + "Build GardeningMCP in the same configuration as this suite.");
        }

        // The server reads its own appsettings, not the suite's.
        // No environment is overridden: it inherits the StoragePath that the fixture has already published, so it reads the database that this run deployed.
        ProcessStartInfo startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(entryPoint),

            // The run reads what the server did from what the instance under test answered, never from what the server said about itself.
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(entryPoint);

        // The probe asks for nothing but a sign that the port is bound.
        using HttpClient httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        // A server started by hand would answer the probe loop below although it reads another database and the child would die unnoticed on the taken port.
        try
        {
            // Any answer at all means that the port is already held.
            using HttpResponseMessage occupiedResponse = await httpClient.GetAsync(Address);
            throw new InvalidOperationException("Port 5006 already answers: stop the GardeningMCP started by hand before running the suite.");
        }
        catch (HttpRequestException)
        {
            // Nothing answers, so the port is free for the server of this run.
        }
        catch (TaskCanceledException)
        {
            // A probe that outlived its own timeout also finds the port free.
        }

        // The process that serves the almanac for the whole run.
        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The gardening server could not be started.");

        // Read to the end and discarded: a child whose output nobody drains blocks once its pipe fills.
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        // The server is waited for until the run's budget for its startup is spent.
        DateTime deadline = DateTime.UtcNow.AddSeconds(startupTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            // A server that has already exited is not waited out: its startup refused the almanac or the port.
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The gardening server exited with code {process.ExitCode} before answering. "
                    + "Port 5006 may be held by another GardeningMCP, such as one started by hand.");
            }

            try
            {
                // Any answer at all, whatever its status, means that the server is bound and ready for the instance under test.
                using HttpResponseMessage response = await httpClient.GetAsync(Address);
                return new GardeningServerHost(process);
            }
            catch (HttpRequestException)
            {
                // The window before the server has bound its port at all.
            }
            catch (TaskCanceledException)
            {
                // A probe that outlived its own timeout; the next one decides.
            }

            // The next probe follows after a short pause.
            await Task.Delay(250);
        }

        // A server that never answered still holds the port for the next invocation.
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        process.Dispose();

        // The budget is spent and nothing answered.
        throw new TimeoutException($"The gardening server did not answer at {Address} within {startupTimeoutSeconds}s.");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            // A server still running holds the fixed port, so it is stopped with everything it started.
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
    }
}
