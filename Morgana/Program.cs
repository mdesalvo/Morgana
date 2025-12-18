using Akka.Actor;
using Akka.Actor.Setup;
using Akka.DependencyInjection;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;
using Morgana.Hubs;
using Morgana.Interfaces;
using Morgana.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// SignalR
builder.Services.AddSignalR();
builder.Services.AddSingleton<ISignalRBridgeService, SignalRBridgeService>();

// CORS (Blazor for Cauldron)
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

// Morgana.AI Services
builder.Services.AddSingleton<ILLMService, AzureOpenAIService>();
//builder.Services.AddSingleton<ILLMService, AnthropicService>();
builder.Services.AddSingleton<IAgentResolverService, HandlesIntentAgentResolverService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();

// Akka.NET Services
builder.Services.AddSingleton(sp =>
{
    BootstrapSetup bootstrap = BootstrapSetup.Create();
    DependencyResolverSetup di = DependencyResolverSetup.Create(sp);
    ActorSystemSetup actorSystemSetup = bootstrap.And(di);
    return ActorSystem.Create("Morgana", actorSystemSetup);
});
builder.Services.AddHostedService<AkkaHostedService>();

WebApplication app = builder.Build();

app.UseCors("AllowBlazor");
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ConversationHub>("/conversationHub");

await app.RunAsync();