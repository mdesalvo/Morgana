using Akka.Actor;

namespace Morgana.Services;

public class AkkaHostedService : IHostedService
{
    private readonly ActorSystem _actorSystem;
    
    public AkkaHostedService(ActorSystem actorSystem)
    {
        _actorSystem = actorSystem;
    }
    
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Actor system già inizializzato nel DI container
        return Task.CompletedTask;
    }
    
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _actorSystem.Terminate();
    }
}