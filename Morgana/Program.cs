using Akka.Actor;
using Akka.Actor.Setup;
using Akka.DependencyInjection;
using Microsoft.Extensions.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Extensions;
using Morgana.AI.Interfaces;
using Morgana.AI.Providers;
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
string llmProvider = builder.Configuration["LLM:Provider"]!;
builder.Services.AddSingleton<ILLMService>(sp =>
{
    IConfiguration config = sp.GetRequiredService<IConfiguration>();
    IPromptResolverService promptResolver = sp.GetRequiredService<IPromptResolverService>();

    return llmProvider.ToLowerInvariant() switch
    {
        "anthropic" => new AnthropicService(config, promptResolver),
        "azureopenai" => new AzureOpenAIService(config, promptResolver),
        _ => throw new InvalidOperationException($"LLM Provider '{llmProvider}' non supportato. Valori validi: 'AzureOpenAI', 'Anthropic'")
    };
});
builder.Services.AddSingleton<IChatClient>(sp =>
    sp.GetRequiredService<ILLMService>().GetChatClient());
builder.Services.AddSingleton<IAgentRegistryService, HandlesIntentAgentRegistryService>();
builder.Services.AddSingleton<IPromptResolverService, ConfigurationPromptResolverService>();

// MCP Protocol Support
builder.Services.AddMCPProtocol(builder.Configuration);
builder.Services.AddSingleton<IMCPToolProvider, MorganaMCPToolProvider>();

// AgentAdapter
builder.Services.AddTransient<AgentAdapter>();

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
