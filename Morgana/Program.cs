using Akka.Actor;
using Akka.Actor.Setup;
using Akka.DependencyInjection;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.Hubs;
using Morgana.Interfaces;
using Morgana.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// SignalR per comunicazione real-time
builder.Services.AddSignalR();

// CORS per Cauldron
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowBlazor", policy =>
    {
        policy.WithOrigins(builder.Configuration["Cauldron:BaseUrl"]!) //Cauldron
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});

// Services
builder.Services.AddSingleton<ILLMService, AzureOpenAIService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();
builder.Services.AddSingleton<ISignalRBridgeService, SignalRBridgeService>();

// Akka.NET Actor System
builder.Services.AddSingleton(sp =>
{
    BootstrapSetup bootstrap = BootstrapSetup.Create();
    DependencyResolverSetup di = DependencyResolverSetup.Create(sp);
    ActorSystemSetup actorSystemSetup = bootstrap.And(di);
    return ActorSystem.Create("Morgana", actorSystemSetup);
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

await app.RunAsync();