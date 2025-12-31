using System.Reflection;
using Morgana.AI.Abstractions;
using Morgana.AI.Attributes;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Registry service that discovers and validates MCP server requirements for Morgana agents.
/// Performs bidirectional validation at startup to ensure configuration consistency.
/// </summary>
public class UsesMCPServersRegistryService : IMCPServerRegistryService
{
    private readonly Dictionary<Type, string[]> agentToServers = [];

    public UsesMCPServersRegistryService(Records.MCPServerConfig[] configuredServers)
    {
        // Discovery: scan all MorganaAgent types with UsesMCPServersAttribute
        IEnumerable<Type> agentsWithMCPRequirements = AppDomain.CurrentDomain.GetAssemblies()
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
            .Where(t => t is { IsClass: true, IsAbstract: false } && 
                       t.IsSubclassOf(typeof(MorganaAgent)) &&
                       t.GetCustomAttribute<UsesMCPServersAttribute>() != null);

        // Build mapping: agent type → server names
        foreach (Type agentType in agentsWithMCPRequirements)
        {
            UsesMCPServersAttribute mcpAttribute = agentType.GetCustomAttribute<UsesMCPServersAttribute>()!;
            agentToServers[agentType] = mcpAttribute.ServerNames;
        }

        // Validation: ensure all requested servers are configured and enabled
        ValidateAgentMCPServerRequirements(configuredServers);
    }

    public string[] GetServerNamesForAgent(Type agentType)
    {
        return agentToServers.GetValueOrDefault(agentType, []);
    }

    public IEnumerable<Type> GetAllAgentsWithMCPServers()
    {
        return agentToServers.Keys;
    }

    /// <summary>
    /// Validates that all MCP servers requested by agents are properly configured and enabled.
    /// Logs warnings for missing servers.
    /// </summary>
    private void ValidateAgentMCPServerRequirements(Records.MCPServerConfig[] configuredServers)
    {
        // Get all available MCP server names (enabled only)
        HashSet<string> availableServers = configuredServers
            .Where(s => s.Enabled)
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Validate each agent's MCP requirements
        foreach (KeyValuePair<Type, string[]> kvp in agentToServers)
        {
            Type agentType = kvp.Key;
            string[] requestedServers = kvp.Value;

            if (requestedServers.Length == 0)
                continue;

            // Check for missing servers
            string[] missingServers = requestedServers
                .Where(requested => !availableServers.Contains(requested))
                .ToArray();

            if (missingServers.Length > 0)
            {
                string agentName = agentType.GetCustomAttribute<HandlesIntentAttribute>()?.Intent ?? agentType.Name;
                
                // Log error - agent won't function properly without required MCP tools
                Console.WriteLine(
                    $"⚠️  MCP VALIDATION WARNING: Agent '{agentName}' requires MCP servers that are not configured or enabled:");
                Console.WriteLine(
                    $"   Missing: {string.Join(", ", missingServers)}");
                Console.WriteLine(
                    $"   Available: {(availableServers.Any() ? string.Join(", ", availableServers) : "none")}");
                Console.WriteLine(
                    $"   → Please enable these servers in appsettings.json under LLM:MCPServers");
                Console.WriteLine();
            }
            else
            {
                string agentName = agentType.GetCustomAttribute<HandlesIntentAttribute>()?.Intent ?? agentType.Name;
                Console.WriteLine(
                    $"✓ MCP Validation: Agent '{agentName}' has all required servers: {string.Join(", ", requestedServers)}");
            }
        }
    }
}