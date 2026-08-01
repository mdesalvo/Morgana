using Akka.Actor;

namespace Morgana.Web.Services;

/// <summary>
/// ASP.NET Core hosted service that manages Akka.NET actor system lifecycle.
/// Actor system initialized in DI container at startup (no action needed here).
/// StartAsync: no-op (actor system already up). StopAsync: gracefully terminates actors for cleanup.
/// </summary>
public class AkkaHostedService : IHostedService
{
    private readonly ActorSystem _actorSystem;

    /// <summary>
    /// Initializes a new instance of the AkkaHostedService.
    /// </summary>
    /// <param name="actorSystem">The Akka.NET actor system to manage (injected from DI)</param>
    public AkkaHostedService(ActorSystem actorSystem)
    {
        _actorSystem = actorSystem;
    }

    /// <summary>
    /// Starts the hosted service.
    /// The actor system is already initialized in the DI container, so no action is needed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for startup cancellation</param>
    /// <returns>Completed task (no async work needed)</returns>
    /// <remarks>
    /// Actor system initialization happens in Program.cs/Startup.cs via:
    /// <code>
    /// services.AddSingleton(ActorSystem.Create("MorganaSystem"));
    /// </code>
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Actor system already initialized in DI container
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the hosted service and gracefully terminates the actor system.
    /// Allows all actors to complete their work and clean up resources.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for shutdown timeout</param>
    /// <returns>Task representing the async termination operation</returns>
    /// <remarks>
    /// <para><strong>Graceful Shutdown:</strong></para>
    /// <list type="bullet">
    /// <item>Sends PoisonPill to all actors in the system</item>
    /// <item>Waits for actors to process remaining messages and stop</item>
    /// <item>Cleans up actor system resources (threads, connections, etc.)</item>
    /// </list>
    /// <para>This prevents message loss and allows actors to persist state before termination.</para>
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _actorSystem.Terminate();
    }
}