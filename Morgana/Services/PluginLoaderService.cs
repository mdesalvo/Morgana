using System.Reflection;
using Morgana.AI.Abstractions;

namespace Morgana.Services;

public class PluginLoaderService
{
    private readonly IConfiguration configuration;
    private readonly ILogger logger;

    public PluginLoaderService(IConfiguration configuration, ILogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    public void LoadPluginAssemblies()
    {
        string[]? assemblyNames = configuration.GetSection("Plugins:Assemblies").Get<string[]>();
        
        if (assemblyNames == null || assemblyNames.Length == 0)
        {
            logger.LogWarning("No plugin assemblies configured");
            return;
        }

        logger.LogInformation($"Loading {assemblyNames.Length} plugin assemblies...");

        foreach (string assemblyName in assemblyNames)
        {
            try
            {
                // Try to load from current directory or standard probing paths
                Assembly assembly = Assembly.Load(new AssemblyName(Path.GetFileNameWithoutExtension(assemblyName)));

                // Validate assembly contains at least one MorganaAgent subclass
                int detectedAgents = assembly.GetTypes()
                    .Count(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaAgent)));
                if (detectedAgents > 0)
                {
                    logger.LogInformation($"✅ Loaded plugin assembly with {detectedAgents} Morgana agents: {assembly.GetName().Name}");
                }
                else
                {
                    logger.LogWarning($"⚠️  Skipped assembly {assemblyName}: no MorganaAgent subclasses found");
                }
            }
            catch (FileNotFoundException)
            {
                logger.LogError($"❌ Plugin assembly not found: {assemblyName}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"❌ Failed to load plugin assembly: {assemblyName}");
            }
        }

        logger.LogInformation("Plugin loading completed");
    }
}