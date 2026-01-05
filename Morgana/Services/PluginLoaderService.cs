using System.Reflection;
using Morgana.AI.Abstractions;

namespace Morgana.Services;

/// <summary>
/// Service for dynamically loading plugin assemblies containing custom Morgana agents.
/// Enables extensibility by loading agent implementations from external assemblies at runtime.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>Allows developers to extend Morgana with custom agents without modifying the core system.
/// Plugins are separate assemblies that contain classes derived from MorganaAgent.</para>
/// <para><strong>Configuration:</strong></para>
/// <para>Plugin assemblies are specified in appsettings.json:</para>
/// <code>
/// {
///   "Plugins": {
///     "Assemblies": [
///       "Morgana.AI.Examples"
///     ]
///   }
/// }
/// </code>
/// <para><strong>Plugin Requirements:</strong></para>
/// <list type="bullet">
/// <item>Assembly must contain at least one class derived from MorganaAgent</item>
/// <item>Assembly must be in the application's probing path (bin directory or GAC)</item>
/// <item>Agents must be properly decorated with [HandlesIntent] attribute for routing</item>
/// </list>
/// <para><strong>Usage:</strong></para>
/// <para>Call LoadPluginAssemblies() during application startup (typically in Program.cs) 
/// before the actor system is used.</para>
/// </remarks>
public class PluginLoaderService
{
    private readonly IConfiguration configuration;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes a new instance of the PluginLoaderService.
    /// </summary>
    /// <param name="configuration">Application configuration for reading plugin settings</param>
    /// <param name="logger">Logger instance for plugin loading diagnostics</param>
    public PluginLoaderService(IConfiguration configuration, ILogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    /// <summary>
    /// Loads all plugin assemblies configured in appsettings.json.
    /// Validates that each assembly contains at least one MorganaAgent subclass.
    /// </summary>
    /// <remarks>
    /// <para><strong>Loading Process:</strong></para>
    /// <list type="number">
    /// <item>Read assembly names from configuration (Plugins:Assemblies section)</item>
    /// <item>Load each assembly using Assembly.Load (searches probing paths)</item>
    /// <item>Validate assembly contains MorganaAgent-derived classes</item>
    /// <item>Log success/failure for each assembly</item>
    /// </list>
    /// <para><strong>Error Handling:</strong></para>
    /// <list type="bullet">
    /// <item><term>FileNotFoundException</term><description>Assembly not found in probing paths</description></item>
    /// <item><term>No agents found</term><description>Assembly loaded but contains no valid MorganaAgent classes</description></item>
    /// <item><term>General exceptions</term><description>Other loading errors (permissions, dependencies, etc.)</description></item>
    /// </list>
    /// <para><strong>Logging Output Examples:</strong></para>
    /// <code>
    /// ✅ Loaded plugin assembly with 3 Morgana agents: "Morgana.AI.Examples"
    /// ⚠️  Skipped assembly CustomLib: no MorganaAgent subclasses found
    /// ❌ Plugin assembly not found: NonExistent.dll
    /// </code>
    /// <para><strong>Best Practice:</strong> Call this method early in application startup, 
    /// after DI configuration but before any actor system usage.</para>
    /// </remarks>
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