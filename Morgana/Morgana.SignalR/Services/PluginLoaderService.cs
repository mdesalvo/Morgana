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
///       "plugins",              // Relative to app base directory (RECOMMENDED)
///       "./custom-plugins",     // Also relative
///       "C:/Morgana/Plugins"    // Absolute path
///     ]
///   }
/// }
/// </code>
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
    /// </summary>
    /// <remarks>
    /// <para><strong>Loading Process:</strong></para>
    /// <list type="number">
    /// <item>Read plugin directories from configuration (Morgana:Plugins:Directories section)</item>
    /// <item>Scan each directory for .dll files</item>
    /// <item>Load each assembly using Assembly.LoadFrom (from filesystem path)</item>
    /// <item>Validate assembly contains MorganaAgent-derived classes</item>
    /// <item>Log success/failure for each assembly</item>
    /// </list>
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
    /// ✅ Loaded plugin assembly with 3 Morgana agents: "Morgana.Example.dll"
    /// ⚠️  Skipped assembly CustomLib.dll: no MorganaAgent subclasses found
    /// ❌ Failed to load invalid.dll: not a valid assembly
    /// </code>
    /// <para><strong>Best Practice:</strong> Call this method early in application startup,
    /// after DI configuration but before any actor system usage.</para>
    /// </remarks>
    public void LoadPluginAssemblies()
    {
        string[]? pluginDirectories = configuration.GetSection("Morgana:Plugins:Directories").Get<string[]>();

        if (pluginDirectories == null || pluginDirectories.Length == 0)
        {
            logger.LogWarning("No plugin directories configured. Morgana will run without custom agents!");
            return;
        }

        logger.LogInformation($"Scanning {pluginDirectories.Length} plugin directories...");

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

                string[] dllFiles = Directory.GetFiles(fullPath, "*.dll", SearchOption.TopDirectoryOnly);

                if (dllFiles.Length == 0)
                {
                    logger.LogInformation($"📭 No .dll files found in {fullPath}");
                    continue;
                }

                foreach (string dllFile in dllFiles)
                {
                    try
                    {
                        // Load assembly from filesystem path
                        Assembly assembly = Assembly.LoadFrom(dllFile);

                        // Validate assembly contains at least one MorganaAgent subclass
                        int detectedAgentsInAssembly = assembly.GetTypes()
                            .Count(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaAgent)));

                        if (detectedAgentsInAssembly > 0)
                        {
                            logger.LogInformation($"✅ Loaded plugin assembly with {detectedAgentsInAssembly} Morgana agents: \"{Path.GetFileName(dllFile)}\"");
                            totalLoaded++;
                            totalAgents += detectedAgentsInAssembly;
                        }
                        else
                        {
                            logger.LogDebug($"⚠️  Skipped assembly {Path.GetFileName(dllFile)}: no MorganaAgent subclasses found");
                        }
                    }
                    catch (BadImageFormatException)
                    {
                        logger.LogWarning($"⚠️  Skipped {Path.GetFileName(dllFile)}: not a valid .NET assembly");
                    }
                    catch (FileLoadException ex)
                    {
                        logger.LogError($"❌ Failed to load {Path.GetFileName(dllFile)}: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, $"❌ Unexpected error loading {Path.GetFileName(dllFile)}");
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