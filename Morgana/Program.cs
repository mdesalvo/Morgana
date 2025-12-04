// ============================================================================
// MORGANA - Sistema Conversazionale Multi-Agente
// ASP.NET Web API (.NET 10) + Akka.NET + Microsoft.Agents.Framework
// ============================================================================

using Akka.Actor;
using Akka.DependencyInjection;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Morgana.Agents;
using Morgana.Interfaces;
using Morgana.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// Azure Services
var storageConnectionString = builder.Configuration["Azure:StorageConnectionString"];
builder.Services.AddSingleton(new TableServiceClient(storageConnectionString));
builder.Services.AddSingleton(new BlobServiceClient(storageConnectionString));
builder.Services.AddApplicationInsightsTelemetry(builder.Configuration["Azure:AppInsightsConnectionString"]);

// Akka.NET Actor System
builder.Services.AddSingleton(sp =>
{
    var bootstrap = BootstrapSetup.Create();
    var di = DependencyResolverSetup.Create(sp);
    var actorSystemSetup = bootstrap.And(di);
    return ActorSystem.Create("MorganaSystem", actorSystemSetup);
});

// Agent Services
builder.Services.AddSingleton<ILLMService, AzureOpenAIService>();
builder.Services.AddSingleton<IStorageService, AzureStorageService>();

var app = builder.Build();

if (app.Environment.IsDevelopment()) { }

app.UseHttpsRedirection();
app.MapControllers();

// Initialize Actors
var actorSystem = app.Services.GetRequiredService<ActorSystem>();
var supervisorProps = DependencyResolver.For(actorSystem).Props<ConversationSupervisorAgent>();
var supervisor = actorSystem.ActorOf(supervisorProps, "conversation-supervisor");

app.Run();