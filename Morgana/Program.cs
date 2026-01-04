using Akka.Actor;
using Akka.Actor.Setup;
using Akka.DependencyInjection;
using Microsoft.Extensions.AI;
using Morgana.AI.Adapters;
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

// Logger
builder.Services.AddSingleton<ILogger>(sp => sp.GetRequiredService<ILoggerFactory>().CreateLogger("Morgana"));

// Plugin Loading - Load domain assemblies dynamically
PluginLoaderService pluginLoaderService = new PluginLoaderService(
    builder.Configuration, builder.Services.BuildServiceProvider().GetRequiredService<ILogger>());
pluginLoaderService.LoadPluginAssemblies();

// Morgana.AI Services
builder.Services.AddSingleton<IToolRegistryService, ProvidesToolForIntentRegistryService>();
builder.Services.AddSingleton<IAgentConfigurationService, EmbeddedAgentConfigurationService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();
builder.Services.AddSingleton<IAgentRegistryService, HandlesIntentAgentRegistryService>();
builder.Services.AddSingleton<ILLMService>(sp => {
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    IPromptResolverService promptResolver = sp.GetRequiredService<IPromptResolverService>();
    string llmProvider = builder.Configuration["LLM:Provider"]!;

    return llmProvider.ToLowerInvariant() switch
    {
        "anthropic" => new AnthropicService(config, promptResolver),
        "azureopenai" => new AzureOpenAIService(config, promptResolver),
        _ => throw new InvalidOperationException($"LLM Provider '{llmProvider}' non supportato. Valori validi: 'AzureOpenAI', 'Anthropic'")
    };
});
builder.Services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<ILLMService>().GetChatClient());

// Agent Adapter
builder.Services.AddSingleton<AgentAdapter>();

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