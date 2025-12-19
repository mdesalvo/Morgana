using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using System.Reflection;

namespace Morgana.AI.Services
{
    public class HandlesIntentAgentRegistryService : IAgentRegistryService
    {
        private readonly Dictionary<string, Type> intentToAgentType = [];

        public HandlesIntentAgentRegistryService(IPromptResolverService promptResolverService)
        {
            // Discovery of available agents with their declared intent

            IEnumerable<Type> morganaAgentTypes = Assembly
                .GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(MorganaAgent)));

            foreach (Type? morganaAgentType in morganaAgentTypes)
            {
                HandlesIntentAttribute? handlesIntentAttribute = morganaAgentType.GetCustomAttribute<HandlesIntentAttribute>();
                if (handlesIntentAttribute != null)
                    intentToAgentType[handlesIntentAttribute.Intent] = morganaAgentType;
            }

            // Bidirectional validation of Morgana agents and classifiable intents

            Records.Prompt classifierPrompt = promptResolverService.ResolveAsync("Classifier").GetAwaiter().GetResult();
            HashSet<string> classifierIntents =
                [.. classifierPrompt.GetAdditionalProperty<List<Dictionary<string, string>>>("Intents")
                                    .SelectMany(dict => dict.Keys)
                                    .Where(key => !string.Equals(key, "other", StringComparison.OrdinalIgnoreCase))];

            HashSet<string> registeredIntents = [.. intentToAgentType.Keys];

            List<string> unregisteredClassifierIntents = [.. classifierIntents.Except(registeredIntents)];
            if (unregisteredClassifierIntents.Count > 0)
                throw new InvalidOperationException($"There are classifier intents not handled by any Morgana agent: {string.Join(", ", unregisteredClassifierIntents)}");

            List<string> unconfiguredAgentIntents = [.. registeredIntents.Except(classifierIntents)];
            if (unconfiguredAgentIntents.Count > 0)
                throw new InvalidOperationException($"There are Morgana agents not configuring their intent for classification: {string.Join(", ", unconfiguredAgentIntents)}");
        }

        public Type? ResolveAgentFromIntent(string intent)
            => intentToAgentType.GetValueOrDefault(intent);

        public IEnumerable<string> GetAllIntents()
            => intentToAgentType.Keys;
    }
}