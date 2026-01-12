using Akka.Actor;

namespace Morgana.SignalR.Services;

/// <summary>
/// ASP.NET Core hosted service for managing the Akka.NET actor system lifecycle.
/// Ensures graceful shutdown of the actor system when the application stops.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>Integrates the Akka.NET actor system with ASP.NET Core's application lifetime management.
/// The actor system is initialized in the DI container before this service starts, and this service
/// ensures proper termination during application shutdown.</para>
/// <para><strong>Lifecycle:</strong></para>
/// <list type="bullet">
/// <item><term>Startup</term><description>Actor system already initialized via DI, no action needed</description></item>
/// <item><term>Shutdown</term><description>Gracefully terminates actor system, allowing actors to clean up</description></item>
/// </list>
/// <para><strong>Registration:</strong></para>
/// <code>
/// services.AddHostedService&lt;AkkaHostedService&gt;();
/// </code>
/// </remarks>
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