using Akka.Actor;

namespace Morgana.Web.Services;

/// <summary>
/// Ties the actor system to the host's lifetime: the container creates it and the host's shutdown terminates it.
/// </summary>
public class AkkaHostedService : IHostedService
{
    /// <summary>The process-wide actor system, created by the container and stopped here.</summary>
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
    /// Starts nothing: the actor system was created by the container in <c>Program.cs</c> when first resolved.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The container created the actor system when it was first resolved: the host has nothing to start.
        return Task.CompletedTask;
    }

    /// <summary>
    /// Terminates the actor system, stopping every conversation's actors, within the host's shutdown timeout.
    /// </summary>
    /// <param name="cancellationToken">Signalled when the host's shutdown timeout expires.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Conversations stop with the host: their actors end instead of being cut off with the process.
        try
        {
            // The actor system is terminated and the wait is bounded by the shutdown's own cancellation.
            await _actorSystem.Terminate().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A termination that outlasts the shutdown timeout is no longer waited for: the host still exits cleanly.
        }
    }
}
