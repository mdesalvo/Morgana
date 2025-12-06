using Akka.Actor;
using Akka.Actor.Setup;
using Akka.DependencyInjection;
using Morgana.Agents;
using Morgana.Hubs;
using Morgana.Interfaces;
using Morgana.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// SignalR per comunicazione real-time
builder.Services.AddSignalR();

// CORS per Morgana.Chat
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazor", policy =>
    {
        policy.WithOrigins("https://localhost:5002", "http://localhost:5003")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Importante per SignalR!
    });
});

// Azure AI Services
builder.Services.AddSingleton<ILLMService, AzureOpenAIService>();

// Storage Services
builder.Services.AddSingleton<IStorageService, AzureStorageService>();

// Application Insights
builder.Services.AddApplicationInsightsTelemetry();

// Akka.NET Actor System
builder.Services.AddSingleton(sp =>
{
    BootstrapSetup bootstrap = BootstrapSetup.Create();
    DependencyResolverSetup di = DependencyResolverSetup.Create(sp);
    ActorSystemSetup actorSystemSetup = bootstrap.And(di);
    
    ActorSystem actorSystem = ActorSystem.Create("MorganaSystem", actorSystemSetup);
    
    // Crea il supervisor root
    Props supervisorProps = DependencyResolver.For(actorSystem).Props<ConversationSupervisorAgent>();
    IActorRef supervisor = actorSystem.ActorOf(supervisorProps, "supervisor");
    
    return actorSystem;
});

// Hosted service per lifecycle Akka.NET
builder.Services.AddHostedService<AkkaHostedService>();

WebApplication app = builder.Build();

app.UseCors("AllowBlazor");
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();

app.MapControllers();
app.MapHub<ConversationHub>("/conversationHub"); // SignalR Hub

app.Run();

// Hosted Service per gestire lifecycle Akka.NET
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