using Morgana.Framework.Abstractions;
using System.Reflection;

namespace Morgana.SignalR.Services;

/// <summary>
/// Service for dynamically loading plugin assemblies containing custom Morgana agents from filesystem.
/// Enables true plugin architecture by loading assemblies from specified directories at runtime.
/// </summary>
/// <remarks>
/// <para><strong>Purpose:</strong></para>
/// <para>Allows developers to extend Morgana with custom agents without modifying the core system
/// or adding assembly references. Plugins are discovered and loaded from filesystem directories.</para>
/// <para><strong>Configuration:</strong></para>
/// <para>Plugin directories are specified in appsettings.json:</para>
/// <code>
/// {
///   "Plugins": {
///     "Directories": [
///       "custom-plugins",       // Additional directories (optional)
///       "C:/Shared/Plugins"     // Absolute paths also supported
///     ]
///   }
/// }
/// </code>
/// <para><strong>Default Behavior:</strong></para>
/// <para>The "plugins" directory is ALWAYS scanned first, even if not configured.
/// If no configuration is provided, only "plugins" is scanned.
/// If "plugins" appears in the configuration, it won't be scanned twice.</para>
/// <para><strong>Path Resolution:</strong></para>
/// <para>Relative paths are resolved against AppDomain.CurrentDomain.BaseDirectory.
/// For a published app in C:\MyApp\, the path "plugins" resolves to C:\MyApp\plugins\</para>
/// <para><strong>Security Warning:</strong></para>
/// <para>⚠️ This approach loads assemblies from filesystem without signature verification.
/// Only load plugins from trusted sources to prevent arbitrary code execution.</para>
/// <para><strong>Plugin Requirements:</strong></para>
/// <list type="bullet">
/// <item>Assembly must contain at least one class derived from MorganaAgent</item>
/// <item>Assembly must be a valid .NET assembly (.dll file)</item>
/// <item>Agents must be properly decorated with [HandlesIntent] attribute for routing</item>
/// <item>All dependencies must be present in plugin directory or GAC</item>
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
    /// Loads all plugin assemblies from configured directories.
    /// Scans each directory for .dll files and validates they contain MorganaAgent subclasses.
    /// The "plugins" directory is always searched first, regardless of configuration.
    /// </summary>
    /// <remarks>
    /// <para><strong>Loading Process:</strong></para>
    /// <list type="number">
    /// <item>Read plugin directories from configuration (Morgana:Plugins:Directories section)</item>
    /// <item>Ensure "plugins" is always the first directory to scan</item>
    /// <item>Scan each directory for .dll files</item>
    /// <item>Load each assembly using Assembly.LoadFrom (from filesystem path)</item>
    /// <item>Validate assembly contains MorganaAgent-derived classes</item>
    /// <item>Log success/failure for each assembly</item>
    /// </list>
    /// <para><strong>Default Behavior:</strong></para>
    /// <para>If no directories are configured, defaults to ["plugins"].</para>
    /// <para><strong>Priority:</strong></para>
    /// <para>"plugins" is always searched first. If configured directories include "plugins",
    /// it won't be scanned twice.</para>
    /// <para><strong>Error Handling:</strong></para>
    /// <list type="bullet">
    /// <item><term>DirectoryNotFoundException</term><description>Plugin directory does not exist</description></item>
    /// <item><term>BadImageFormatException</term><description>File is not a valid .NET assembly</description></item>
    /// <item><term>No agents found</term><description>Assembly loaded but contains no valid MorganaAgent classes</description></item>
    /// <item><term>General exceptions</term><description>Other loading errors (permissions, dependencies, etc.)</description></item>
    /// </list>
    /// <para><strong>Logging Output Examples:</strong></para>
    /// <code>
    /// 📁 Scanning plugin directory: ./plugins
    /// ✅ Loaded plugin assembly with 3 Morgana agents: "Morgana.Examples.dll"
    /// ⚠️  Skipped assembly CustomLib.dll: no MorganaAgent subclasses found
    /// ❌ Failed to load invalid.dll: not a valid assembly
    /// </code>
    /// <para><strong>Best Practice:</strong> Call this method early in application startup,
    /// after DI configuration but before any actor system usage.</para>
    /// </remarks>
    public void LoadPluginAssemblies()
    {
        string[]? configuredDirectories = configuration.GetSection("Morgana:Plugins:Directories").Get<string[]>();

        // Build the final list of directories with "plugins" always first
        List<string> pluginDirectories = [ "plugins" ];
        
        // Add other configured directories (skip if "plugins" is already in the list)
        if (configuredDirectories is { Length: > 0 })
        {
            foreach (string configuredDirectory in configuredDirectories)
            {
                // Normalize path for comparison (handle "./plugins", "plugins", etc.)
                string normalizedDirectory = configuredDirectory.TrimStart('.', '/', '\\').TrimStart('/', '\\');
                string normalizedPlugins = "plugins".TrimStart('.', '/', '\\').TrimStart('/', '\\');
                
                if (!normalizedDirectory.Equals(normalizedPlugins, StringComparison.OrdinalIgnoreCase))
                    pluginDirectories.Add(configuredDirectory);
            }
        }

        logger.LogInformation($"Scanning {pluginDirectories.Count} plugin directories (priority order)...");

        int totalLoaded = 0;
        int totalAgents = 0;

        foreach (string pluginDirectory in pluginDirectories)
        {
            try
            {
                // Resolve path relative to application base directory
                string fullPath = Path.IsPathRooted(pluginDirectory) 
                    ? pluginDirectory 
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, pluginDirectory);
                
                fullPath = Path.GetFullPath(fullPath);
                
                if (!Directory.Exists(fullPath))
                {
                    logger.LogWarning($"⚠️  Plugin directory not found: {fullPath}");
                    continue;
                }

                logger.LogInformation($"📁 Scanning plugin directory: {fullPath}");

                string[] pluginAssemblies = Directory.GetFiles(fullPath, "*.dll", SearchOption.TopDirectoryOnly);

                if (pluginAssemblies.Length == 0)
                {
                    logger.LogInformation($"📭 No .dll files found in {fullPath}");
                    continue;
                }

                foreach (string pluginAssembly in pluginAssemblies)
                {
                    try
                    {
                        // Load assembly from filesystem path
                        Assembly assembly = Assembly.LoadFrom(pluginAssembly);

                        // Validate assembly contains at least one MorganaAgent subclass
                        int detectedAgents = assembly.GetTypes()
                            .Count(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaAgent)));

                        if (detectedAgents > 0)
                        {
                            logger.LogInformation($"✅ Loaded plugin assembly with {detectedAgents} Morgana agents: \"{Path.GetFileName(pluginAssembly)}\"");
                            totalLoaded++;
                            totalAgents += detectedAgents;
                        }
                        else
                        {
                            logger.LogDebug($"⚠️  Skipped assembly {Path.GetFileName(pluginAssembly)}: no MorganaAgent subclasses found");
                        }
                    }
                    catch (BadImageFormatException)
                    {
                        logger.LogWarning($"⚠️  Skipped {Path.GetFileName(pluginAssembly)}: not a valid .NET assembly");
                    }
                    catch (FileLoadException ex)
                    {
                        logger.LogError($"❌ Failed to load {Path.GetFileName(pluginAssembly)}: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, $"❌ Unexpected error loading {Path.GetFileName(pluginAssembly)}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"❌ Failed to scan directory: {pluginDirectory}");
            }
        }

        logger.LogInformation($"Plugin loading completed: {totalLoaded} assemblies loaded, {totalAgents} total agents discovered");
    }
}