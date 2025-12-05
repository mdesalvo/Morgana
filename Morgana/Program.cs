using Akka.Actor;
using Akka.Actor.Setup;
using Akka.DependencyInjection;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Morgana.Agents;
using Morgana.Interfaces;
using Morgana.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Azure Services
string? storageConnectionString = builder.Configuration["Azure:StorageConnectionString"];
builder.Services.AddSingleton(new TableServiceClient(storageConnectionString));
builder.Services.AddSingleton(new BlobServiceClient(storageConnectionString));
builder.Services.AddApplicationInsightsTelemetry(builder.Configuration["Azure:AppInsightsConnectionString"]);

// Akka.NET Actor System
builder.Services.AddSingleton(sp =>
{
    BootstrapSetup bootstrap = BootstrapSetup.Create();
    DependencyResolverSetup di = DependencyResolverSetup.Create(sp);
    ActorSystemSetup actorSystemSetup = bootstrap.And(di);
    return ActorSystem.Create("MorganaSystem", actorSystemSetup);
});

// Agent Services
builder.Services.AddSingleton<ILLMService, AzureOpenAIService>();
builder.Services.AddSingleton<IStorageService, AzureStorageService>();

WebApplication app = builder.Build();

if (app.Environment.IsDevelopment()) { }

app.UseHttpsRedirection();
app.MapControllers();

// Initialize Actors
ActorSystem actorSystem = app.Services.GetRequiredService<ActorSystem>();
Props supervisorProps = DependencyResolver.For(actorSystem).Props<ConversationSupervisorAgent>();
IActorRef supervisor = actorSystem.ActorOf(supervisorProps, "conversation-supervisor");

app.Run();