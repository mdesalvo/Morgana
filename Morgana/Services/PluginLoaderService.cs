using System.Reflection;

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
                logger.LogInformation($"✅ Loaded plugin assembly: {assembly.GetName().Name}");
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