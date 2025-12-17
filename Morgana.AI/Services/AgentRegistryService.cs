using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;
using System.Reflection;

namespace Morgana.AI.Services
{
    public class AgentRegistryService : IAgentRegistryService
    {
        private readonly Dictionary<string, Type> intentToAgentType = [];

        public AgentRegistryService()
        {
            IEnumerable<Type> agentTypes = Assembly
                .GetExecutingAssembly()
                .GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && t.IsSubclassOf(typeof(MorganaAgent)));

            foreach (Type? agentType in agentTypes)
            {
                HandlesIntentAttribute? attr = agentType.GetCustomAttribute<HandlesIntentAttribute>();
                if (attr != null)
                {
                    intentToAgentType[attr.Intent] = agentType;
                }
            }
        }

        public Type? GetAgentType(string intent)
            => intentToAgentType.GetValueOrDefault(intent);

        public IEnumerable<string> GetRegisteredIntents()
            => intentToAgentType.Keys;
    }
}