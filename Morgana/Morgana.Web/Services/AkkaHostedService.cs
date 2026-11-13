using Akka.Actor;

namespace Morgana.Web.Services;

/// <summary>
/// Terminates the actor system when the host stops.
/// The host resolves this service at start, which builds the actor system it is given, so starting needs nothing more.
/// </summary>
public class AkkaHostedService : IHostedService
{
    /// <summary>The actor system every conversation's actors run in.</summary>
    private readonly ActorSystem _actorSystem;

    /// <summary>Binds the service to the actor system it terminates.</summary>
    /// <param name="actorSystem">The container's actor system.</param>
    public AkkaHostedService(ActorSystem actorSystem)
    {
        _actorSystem = actorSystem;
    }

    /// <summary>Does nothing: resolving this service at host start already built the actor system.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>Terminates the actor system, stopping every conversation's actors before the process exits.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Each actor's PostStop runs, so a supervisor caught mid-turn still closes its turn span.
        await _actorSystem.Terminate();
    }
}