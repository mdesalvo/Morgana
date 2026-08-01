using System.Reflection;
using Morgana.AI.Abstractions;

namespace Morgana.Web.Services;

/// <summary>
/// Dynamically loads plugin assemblies from filesystem directories at startup (enables plugin architecture).
/// Configuration: Morgana:Plugins:Directories in appsettings.json (relative/absolute paths supported).
/// Always scans "plugins" directory first. Plugins must contain MorganaAgent subclasses decorated with [HandlesIntent].
/// Security warning: loads assemblies without signature verification — only from trusted sources to prevent code execution.
/// </summary>
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
    /// Loads all plugin assemblies from configured directories (Morgana:Plugins:Directories), validating each
    /// contains MorganaAgent-derived classes. Defaults to ["plugins"]. Always scans "plugins" first; no duplicates.
    /// Handles errors: DirectoryNotFound, BadImageFormat, no agents found, other loading errors. Logs success/failure.
    /// </summary>
    public void LoadPluginAssemblies()
    {
        string[]? configuredDirectories = configuration.GetSection("Morgana:Plugins:Directories").Get<string[]>();

        // Build the final list of directories with "plugins" always first
        List<string> pluginDirectories = [ "plugins" ];

        // Add other configured directories (skip if "plugins" is already in the list)
        if (configuredDirectories is { Length: > 0 })
        {
            pluginDirectories.AddRange(
                from configuredDirectory
                in configuredDirectories
                let normalizedDirectory = configuredDirectory.TrimStart('.', '/', '\\').TrimStart('/', '\\')
                let normalizedPlugins = "plugins".TrimStart('.', '/', '\\').TrimStart('/', '\\')
                where !string.Equals(normalizedDirectory, normalizedPlugins, StringComparison.OrdinalIgnoreCase)
                select configuredDirectory);
        }

        logger.LogInformation("Scanning {PluginDirectoriesCount} plugin directories (priority order)...", pluginDirectories.Count);

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
                    logger.LogWarning("⚠️  Plugin directory not found: {FullPath}", fullPath);
                    continue;
                }

                logger.LogInformation("📁 Scanning plugin directory: {FullPath}", fullPath);

                string[] pluginAssemblies = Directory.GetFiles(fullPath, "*.dll", SearchOption.TopDirectoryOnly);

                if (pluginAssemblies.Length == 0)
                {
                    logger.LogInformation("📭 No .dll files found in {FullPath}", fullPath);
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
                            logger.LogInformation("✅ Loaded plugin assembly with {DetectedAgents} Morgana agents: \"{GetFileName}\"", detectedAgents, Path.GetFileName(pluginAssembly));
                            totalLoaded++;
                            totalAgents += detectedAgents;
                        }
                        else
                        {
                            logger.LogDebug("⚠️  Skipped assembly {GetFileName}: no MorganaAgent subclasses found", Path.GetFileName(pluginAssembly));
                        }
                    }
                    catch (BadImageFormatException)
                    {
                        logger.LogWarning("⚠️  Skipped {GetFileName}: not a valid .NET assembly", Path.GetFileName(pluginAssembly));
                    }
                    catch (FileLoadException ex)
                    {
                        logger.LogError("❌ Failed to load {GetFileName}: {ExMessage}", Path.GetFileName(pluginAssembly), ex.Message);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "❌ Unexpected error loading {GetFileName}", Path.GetFileName(pluginAssembly));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "❌ Failed to scan directory: {PluginDirectory}", pluginDirectory);
            }
        }

        logger.LogInformation("Plugin loading completed: {TotalLoaded} assemblies loaded, {TotalAgents} total agents discovered", totalLoaded, totalAgents);
    }
}