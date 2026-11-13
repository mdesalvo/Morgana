using System.Reflection;
using Morgana.AI.Abstractions;

namespace Morgana.Web.Services;

/// <summary>
/// Loads the plugin assemblies at startup: the domains' agents in one pass, the LLM providers in another, over the same directories.
/// The directories are "plugins" first, then those of <c>Morgana:Plugins:Directories</c>; assemblies are loaded without signature
/// verification, so a deployment puts there only what it trusts.
/// </summary>
public class PluginLoaderService
{
    /// <summary>The default directory, scanned first whatever the configuration declares.</summary>
    private const string DefaultPluginDirectory = "plugins";

    /// <summary>Holds the plugin directories that a deployment declares.</summary>
    private readonly IConfiguration configuration;

    /// <summary>Records what each scan found or skipped.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the loader to the configuration that declares the directories.</summary>
    /// <param name="configuration">The application configuration.</param>
    /// <param name="logger">Records what each scan found or skipped.</param>
    public PluginLoaderService(IConfiguration configuration, ILogger logger)
    {
        this.configuration = configuration;
        this.logger = logger;
    }

    /// <summary>
    /// Loads every assembly of the plugin directories and reports the agents that they carry.
    /// Tools and agent configurations arrive with the same assemblies and are discovered by their own registries.
    /// </summary>
    public void LoadAgents()
    {
        // The registries find the agents in the loaded assemblies; this pass only has to load them and say what it found.
        IReadOnlyList<Type> agentTypes = LoadPluginTypes(typeof(MorganaAgent), "agent");

        logger.LogInformation("Plugin loading completed: {TotalAgents} total agents discovered", agentTypes.Count);
    }

    /// <summary>Loads the providers carried by the plugin directories and returns their types for the host to register.</summary>
    public IReadOnlyList<Type> LoadLanguageModels()
    {
        // The host registers each of these before the LLM service is built, which then indexes them by name.
        IReadOnlyList<Type> languageModelTypes = LoadPluginTypes(typeof(MorganaLanguageModel), "language model");

        logger.LogInformation("Language model loading completed: {TotalLanguageModels} total providers discovered", languageModelTypes.Count);

        // The types go to the host, which registers them before the LLM service is built.
        return languageModelTypes;
    }

    /// <summary>
    /// Loads every assembly at the top of each plugin directory and returns the concrete subclasses of <paramref name="pluginBaseType"/> they carry.
    /// One broken file or unreadable directory costs only itself, so the boot always reaches its own checks.
    /// </summary>
    /// <param name="pluginBaseType">The base every plugin type of this pass derives from.</param>
    /// <param name="pluginKind">What the pass loads, named in the log lines.</param>
    private List<Type> LoadPluginTypes(Type pluginBaseType, string pluginKind)
    {
        // The plugin types found across every directory, in scan order.
        List<Type> pluginTypes = [];

        // Each directory is scanned in turn, in priority order.
        foreach (string fullPath in ResolvePluginDirectories())
        {
            // A directory that cannot be scanned costs the plugins in it only: the others still load.
            try
            {
                logger.LogInformation("📁 Scanning plugin directory for {PluginKind} assemblies: {FullPath}", pluginKind, fullPath);

                // Only the top level is scanned: a plugin's dependencies sit beside it and are loaded on demand.
                string[] pluginAssemblies = Directory.GetFiles(fullPath, "*.dll", SearchOption.TopDirectoryOnly);

                foreach (string pluginAssembly in pluginAssemblies)
                {
                    // One broken file never stops the others from loading.
                    try
                    {
                        // Into the process's default load context, where every registry scanning the loaded assemblies finds it.
                        Assembly assembly = Assembly.LoadFrom(pluginAssembly);

                        // Concrete types only: the abstract bases are the scaffolding that an author derives from.
                        Type[] detectedPluginTypes =
                        [
                            .. assembly.GetTypes()
                                .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsSubclassOf(pluginBaseType))
                        ];

                        // An assembly without one is a dependency sitting beside a plugin, such as a vendor SDK: loaded, not counted.
                        if (detectedPluginTypes.Length == 0)
                        {
                            logger.LogDebug("⚠️  Skipped assembly {GetFileName}: no {PluginBaseType} subclasses found", Path.GetFileName(pluginAssembly), pluginBaseType.Name);
                            continue;
                        }

                        logger.LogInformation("✅ Loaded {PluginKind} assembly with {DetectedPluginTypes} plugin types: \"{GetFileName}\"", pluginKind, detectedPluginTypes.Length, Path.GetFileName(pluginAssembly));

                        // The assembly's plugin types join those of the files before it.
                        pluginTypes.AddRange(detectedPluginTypes);
                    }
                    catch (BadImageFormatException)
                    {
                        // A native or corrupted library next to the plugins: skipped with a warning, since it is not a plugin.
                        logger.LogWarning("⚠️  Skipped {GetFileName}: not a valid .NET assembly", Path.GetFileName(pluginAssembly));
                    }
                    catch (FileLoadException ex)
                    {
                        // A plugin that cannot be loaded leaves out what it carries: the startup checks then report an intent
                        // without its agent or a tier naming an unregistered provider.
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
                logger.LogError(ex, "❌ Failed to scan directory: {PluginDirectory}", fullPath);
            }
        }

        // Every plugin type of this pass, for the caller to report or register.
        return pluginTypes;
    }

    /// <summary>Resolves the plugin directories to full paths in priority order: "plugins" first then the configured ones without duplicates.</summary>
    private List<string> ResolvePluginDirectories()
    {
        // Null when the deployment declares no directory: the default one is then the only place scanned.
        string[]? configuredDirectories = configuration.GetSection("Morgana:Plugins:Directories").Get<string[]>();

        // The default directory leads, so a domain shipped in it is always found.
        List<string> pluginDirectories = [DefaultPluginDirectory];

        // The configured directories join it, skipping one that names the same folder: leading dots and separators
        // are ignored, so "./plugins" and "plugins" are one directory, scanned once.
        if (configuredDirectories is { Length: > 0 })
        {
            pluginDirectories.AddRange(
                configuredDirectories.Where(configuredDirectory =>
                    !string.Equals(configuredDirectory.TrimStart('.', '/', '\\'), DefaultPluginDirectory, StringComparison.OrdinalIgnoreCase)));
        }

        // The directories that exist, as full paths, in the order they are scanned.
        List<string> fullPaths = [];

        foreach (string pluginDirectory in pluginDirectories)
        {
            // A directory that cannot be resolved costs only itself: the others are still scanned.
            try
            {
                // A relative directory is read from the application's own folder, never from the working directory it was started in.
                string fullPath = Path.IsPathRooted(pluginDirectory)
                    ? pluginDirectory
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, pluginDirectory);

                // The path is made absolute, so the same folder reads alike whatever spelling the configuration gave it.
                fullPath = Path.GetFullPath(fullPath);

                // A directory that is absent is reported and skipped: the default one is optional too.
                if (!Directory.Exists(fullPath))
                {
                    logger.LogWarning("⚠️  Plugin directory not found: {FullPath}", fullPath);
                    continue;
                }

                // An existing directory joins the scan, in its priority place.
                fullPaths.Add(fullPath);
            }
            catch (Exception ex)
            {
                // An unresolvable directory is reported and the scan goes on with the next one.
                logger.LogError(ex, "❌ Failed to resolve directory: {PluginDirectory}", pluginDirectory);
            }
        }

        // Both passes read the same list, so they cannot disagree on where plugins live.
        return fullPaths;
    }
}