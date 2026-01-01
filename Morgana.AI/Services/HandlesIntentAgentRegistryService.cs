using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using System.Reflection;

namespace Morgana.AI.Services;

public class HandlesIntentAgentRegistryService : IAgentRegistryService
{
    private readonly IPromptResolverService promptResolverService;
    private readonly Lazy<Dictionary<string, Type>> intentToAgentType;

    public HandlesIntentAgentRegistryService(IPromptResolverService promptResolverService)
    {
        this.promptResolverService = promptResolverService;
        
        // Lazy initialization - scan assemblies only when first accessed
        // This ensures all assemblies (including Morgana.AI.Examples) are loaded
        intentToAgentType = new Lazy<Dictionary<string, Type>>(InitializeAgentRegistry);
    }

    private Dictionary<string, Type> InitializeAgentRegistry()
    {
        Dictionary<string, Type> registry = new();
        
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

        // Bidirectional validation of Morgana agents and classifiable intents
        Records.Prompt classifierPrompt = promptResolverService.ResolveAsync("Classifier").GetAwaiter().GetResult();
        
        // Parse structured IntentDefinition objects
        List<Records.IntentDefinition> allIntents = classifierPrompt.GetAdditionalProperty<List<Records.IntentDefinition>>("Intents");
        
        // Extract intent names, excluding "other"
        HashSet<string> classifierIntents = allIntents
            .Where(intent => !string.Equals(intent.Name, "other", StringComparison.OrdinalIgnoreCase))
            .Select(intent => intent.Name)
            .ToHashSet();

        HashSet<string> registeredIntents = [.. registry.Keys];

        List<string> unregisteredClassifierIntents = [.. classifierIntents.Except(registeredIntents)];
        if (unregisteredClassifierIntents.Count > 0)
            throw new InvalidOperationException($"There are classifier intents not handled by any Morgana agent: {string.Join(", ", unregisteredClassifierIntents)}");

        List<string> unconfiguredAgentIntents = [.. registeredIntents.Except(classifierIntents)];
        if (unconfiguredAgentIntents.Count > 0)
            throw new InvalidOperationException($"There are Morgana agents not configuring their intent for classification: {string.Join(", ", unconfiguredAgentIntents)}");
        
        return registry;
    }

    public Type? ResolveAgentFromIntent(string intent)
        => intentToAgentType.Value.GetValueOrDefault(intent);

    public IEnumerable<string> GetAllIntents()
        => intentToAgentType.Value.Keys;
}