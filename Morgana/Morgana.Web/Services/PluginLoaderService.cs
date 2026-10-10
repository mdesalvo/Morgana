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
    /// <summary>Holds the plugin directories that a deployment declares.</summary>
    private readonly IConfiguration configuration;

    /// <summary>Records what each scan found or skipped.</summary>
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
        // Null when the deployment declares no directory: the default one is then the only place scanned.
        string[]? configuredDirectories = configuration.GetSection("Morgana:Plugins:Directories").Get<string[]>();

        // Build the final list of directories with "plugins" always first
        List<string> pluginDirectories = [ "plugins" ];

        // Add other configured directories (skip if "plugins" is already in the list).
        // The match ignores leading dots and separators, so "./plugins" and "plugins" are the same directory.
        if (configuredDirectories is { Length: > 0 })
        {
            // The configured directories join the default one, skipping any that name the same folder.
            pluginDirectories.AddRange(
                from configuredDirectory
                in configuredDirectories
                let normalizedDirectory = configuredDirectory.TrimStart('.', '/', '\\').TrimStart('/', '\\')
                let normalizedPlugins = "plugins".TrimStart('.', '/', '\\').TrimStart('/', '\\')
                where !string.Equals(normalizedDirectory, normalizedPlugins, StringComparison.OrdinalIgnoreCase)
                select configuredDirectory);
        }

        logger.LogInformation("Scanning {PluginDirectoriesCount} plugin directories (priority order)...", pluginDirectories.Count);

        // Totals for the closing line that tells the operator what the installation can serve.
        int totalLoaded = 0;
        int totalAgents = 0;

        // Each directory is scanned in turn, in priority order.
        foreach (string pluginDirectory in pluginDirectories)
        {
            // A directory that cannot be scanned costs the plugins in it only: the others still load.
            try
            {
                // Resolve path relative to application base directory
                string fullPath = Path.IsPathRooted(pluginDirectory)
                    ? pluginDirectory
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, pluginDirectory);

                // The path is made absolute, so the same folder reads alike whatever spelling the configuration gave it.
                fullPath = Path.GetFullPath(fullPath);

                // A declared directory that is absent is skipped, without stopping the scan of the others.
                if (!Directory.Exists(fullPath))
                {
                    // A declared directory that is absent is reported and skipped: the default one is optional too.
                    logger.LogWarning("⚠️  Plugin directory not found: {FullPath}", fullPath);
                    continue;
                }

                logger.LogInformation("📁 Scanning plugin directory: {FullPath}", fullPath);

                // Only the top level is scanned: a plugin's dependencies sit beside it and are loaded on demand.
                string[] pluginAssemblies = Directory.GetFiles(fullPath, "*.dll", SearchOption.TopDirectoryOnly);

                // A directory with no assemblies holds no plugin, so it is skipped.
                if (pluginAssemblies.Length == 0)
                {
                    logger.LogInformation("📭 No .dll files found in {FullPath}", fullPath);
                    continue;
                }

                foreach (string pluginAssembly in pluginAssemblies)
                {
                    // One broken file never stops the others from loading.
                    try
                    {
                        // Load assembly from filesystem path
                        Assembly assembly = Assembly.LoadFrom(pluginAssembly);

                        // Validate assembly contains at least one MorganaAgent subclass
                        int detectedAgents = assembly.GetTypes()
                            .Count(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaAgent)));

                        // An assembly with no agent is not a plugin, so it is not counted.
                        // A dependency of a plugin sits beside it: it is loaded when needed and is not an agent library.
                        if (detectedAgents == 0)
                        {
                            logger.LogDebug("⚠️  Skipped assembly {GetFileName}: no MorganaAgent subclasses found", Path.GetFileName(pluginAssembly));
                            continue;
                        }

                        logger.LogInformation("✅ Loaded plugin assembly with {DetectedAgents} Morgana agents: \"{GetFileName}\"", detectedAgents, Path.GetFileName(pluginAssembly));
                        totalLoaded++;
                        totalAgents += detectedAgents;
                    }
                    catch (BadImageFormatException)
                    {
                        // A native or corrupted library next to the plugins: skipped with a warning, since it is not a plugin.
                        logger.LogWarning("⚠️  Skipped {GetFileName}: not a valid .NET assembly", Path.GetFileName(pluginAssembly));
                    }
                    catch (FileLoadException ex)
                    {
                        // A plugin that cannot be loaded leaves its agents out: the registry's checks report what then lacks an agent.
                        logger.LogError("❌ Failed to load {GetFileName}: {ExMessage}", Path.GetFileName(pluginAssembly), ex.Message);
                    }
                    catch (Exception ex)
                    {
                        // Any other fault, a type that fails to resolve included, is absorbed so the boot reaches its own checks.
                        logger.LogError(ex, "❌ Unexpected error loading {GetFileName}", Path.GetFileName(pluginAssembly));
                    }
                }
            }
            catch (Exception ex)
            {
                // An unreadable directory is reported and the scan goes on with the next one.
                logger.LogError(ex, "❌ Failed to scan directory: {PluginDirectory}", pluginDirectory);
            }
        }

        logger.LogInformation("Plugin loading completed: {TotalLoaded} assemblies loaded, {TotalAgents} total agents discovered", totalLoaded, totalAgents);
    }
}