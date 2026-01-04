using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using System.Reflection;

namespace Morgana.AI.Services;

public class HandlesIntentAgentRegistryService : IAgentRegistryService
{
    private readonly IAgentConfigurationService agentConfigService;
    private readonly Dictionary<string, Type> intentToAgentType;

    public HandlesIntentAgentRegistryService(IAgentConfigurationService agentConfigService)
    {
        this.agentConfigService = agentConfigService;

        intentToAgentType = InitializeRegistry();
    }

    private Dictionary<string, Type> InitializeRegistry()
    {
        Dictionary<string, Type> registry = new(StringComparer.OrdinalIgnoreCase);
        
        // Discovery of available agents with their declared intent
        // Scan ALL loaded assemblies, not just executing assembly
        IEnumerable<Type> morganaAgentTypes = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic)
            .SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch (ReflectionTypeLoadException)
                {
                    return [];
                }
            })
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.IsSubclassOf(typeof(MorganaAgent)));
        foreach (Type? morganaAgentType in morganaAgentTypes)
        {
            HandlesIntentAttribute? handlesIntentAttribute = morganaAgentType.GetCustomAttribute<HandlesIntentAttribute>();
            if (handlesIntentAttribute != null)
                registry[handlesIntentAttribute.Intent] = morganaAgentType;
        }

        #region Validation
        // Bidirectional validation of Morgana agents and intents
        // Load intents from domain-specific configuration
        List<Records.IntentDefinition> allIntents = agentConfigService.GetIntentsAsync().GetAwaiter().GetResult();

        // Extract intent names, excluding "other"
        HashSet<string> classifierIntents = allIntents
            .Where(intent => !string.Equals(intent.Name, "other", StringComparison.OrdinalIgnoreCase))
            .Select(intent => intent.Name)
            .ToHashSet();

        HashSet<string> registeredIntents = [.. registry.Keys];

        List<string> unregisteredClassifierIntents = [.. classifierIntents.Except(registeredIntents)];
        if (unregisteredClassifierIntents.Count > 0)
            throw new InvalidOperationException(
                $"There are intents not handled by any Morgana agent: {string.Join(", ", unregisteredClassifierIntents)}");

        List<string> unconfiguredAgentIntents = [.. registeredIntents.Except(classifierIntents)];
        if (unconfiguredAgentIntents.Count > 0)
            throw new InvalidOperationException(
                $"There are Morgana agents handling an undeclared intent: {string.Join(", ", unconfiguredAgentIntents)}");
        #endregion

        return registry;
    }

    public Type? ResolveAgentFromIntent(string intent)
        => intentToAgentType.GetValueOrDefault(intent);

    public IEnumerable<string> GetAllIntents()
        => intentToAgentType.Keys;
}