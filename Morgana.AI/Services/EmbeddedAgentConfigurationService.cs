using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Loads agent configuration from agents.json embedded resource.
/// Scans all loaded assemblies to find agents.json and loads domain-specific configuration.
/// Provides graceful fallback if agents.json is not found.
/// </summary>
public class EmbeddedAgentConfigurationService : IAgentConfigurationService
{
    private readonly AgentConfiguration configuration;
    private readonly ILogger logger;
    
    public EmbeddedAgentConfigurationService(ILogger logger)
    {
        this.logger = logger;

        configuration = LoadAgentConfiguration();
    }
    
    public Task<List<Records.IntentDefinition>> GetIntentsAsync()
    {
        return Task.FromResult(configuration.Intents);
    }
    
    public Task<List<Records.Prompt>> GetAgentPromptsAsync()
    {
        return Task.FromResult(configuration.Agents);
    }
    
    private AgentConfiguration LoadAgentConfiguration()
    {
        logger.LogInformation("Searching for agents.json in loaded assemblies...");
        
        // Search for agents.json in ALL loaded assemblies
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic))
        {
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(".agents.json", StringComparison.OrdinalIgnoreCase));
            
            if (resourceName != null)
            {
                logger.LogInformation($"✅ Found agents.json in assembly: {assembly.GetName().Name}");
                
                try
                {
                    using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                    if (stream == null)
                    {
                        logger.LogWarning($"Could not open stream for {resourceName}");
                        continue;
                    }
                    
                    AgentConfiguration? config = JsonSerializer.Deserialize<AgentConfiguration>(
                        stream, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (config != null)
                    {
                        logger.LogInformation(
                            $"✅ Loaded {config.Intents.Count} intents and {config.Agents.Count} agent prompts from agents.json");
                        
                        // Log loaded intents for debugging
                        foreach (Records.IntentDefinition intent in config.Intents)
                        {
                            logger.LogInformation($"   📝 Intent: {intent.Name} - {intent.Description}");
                        }
                        
                        return config;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Failed to deserialize agents.json from {assembly.GetName().Name}");
                }
            }
        }
        
        // Fallback: no agents.json found
        logger.LogWarning(
            "⚠️  No agents.json found in any loaded assembly. " +
            "Classifier and presentation will have no intents available. " +
            "Add agents.json as embedded resource to your domain project.");
        
        return new AgentConfiguration([], []);
    }
    
    /// <summary>
    /// Internal record for deserializing agents.json structure.
    /// </summary>
    private record AgentConfiguration(
        List<Records.IntentDefinition> Intents,
        List<Records.Prompt> Agents);
}