using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Providers;

/// <summary>
/// Orchestrates MCP tool loading and conversion to AIFunction format.
/// Manages multiple MCP servers and provides unified tool access to Morgana agents.
/// Bridges MCP protocol to Microsoft.Extensions.AI function calling.
/// </summary>
public class MorganaMCPToolProvider : IMCPToolProvider
{
    private readonly Dictionary<string, IMCPServer> mcpServers;
    private readonly ILogger logger;
    
    public MorganaMCPToolProvider(
        IEnumerable<IMCPServer> servers,
        ILogger logger)
    {
        this.logger = logger;
        
        // Use case-insensitive dictionary for server name lookups
        mcpServers = servers.ToDictionary(
            s => s.ServerName, 
            s => s, 
            StringComparer.OrdinalIgnoreCase);
        
        logger.LogInformation($"MorganaMCPToolProvider initialized with {mcpServers.Count} servers: {string.Join(", ", mcpServers.Keys)}");
    }
    
    public async Task<IEnumerable<AIFunction>> LoadToolsFromServerAsync(string serverName)
    {
        // TryGetValue now works case-insensitively thanks to StringComparer.OrdinalIgnoreCase
        if (!mcpServers.TryGetValue(serverName, out IMCPServer? server))
        {
            logger.LogWarning($"MCP server not found: {serverName}. Available servers: {string.Join(", ", mcpServers.Keys)}");
            return [];
        }
        
        IEnumerable<Records.MCPToolDefinition> mcpTools = await server.ListToolsAsync();
        List<AIFunction> aiFunctions = [];
        
        foreach (Records.MCPToolDefinition mcpTool in mcpTools)
        {
            AIFunction aiFunction = ConvertToAIFunction(server, mcpTool);
            aiFunctions.Add(aiFunction);
        }
        
        logger.LogInformation($"Loaded {aiFunctions.Count} tools from MCP server '{serverName}'");
        
        return aiFunctions;
    }
    
    public async Task<IEnumerable<AIFunction>> LoadAllToolsAsync()
    {
        List<AIFunction> allFunctions = new List<AIFunction>();
        
        foreach (IMCPServer server in mcpServers.Values)
        {
            IEnumerable<AIFunction> functions = await LoadToolsFromServerAsync(server.ServerName);
            allFunctions.AddRange(functions);
        }
        
        logger.LogInformation($"Loaded {allFunctions.Count} tools from all MCP servers");
        return allFunctions;
    }

    public IEnumerable<string> GetRegisteredServers()
    {
        return mcpServers.Keys;
    }
    
    private AIFunction ConvertToAIFunction(IMCPServer server, Records.MCPToolDefinition mcpTool)
    {
        // Create async delegate that invokes MCP server
        Func<Dictionary<string, object>, Task<string>> implementation = async (parameters) =>
        {
            try
            {
                logger.LogInformation($"🔧 MCP Tool Call START: {mcpTool.Name}");
                logger.LogInformation($"   Server: {server.GetType().Name}");
                logger.LogInformation($"   Parameters received: {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))}");
            
                Records.MCPToolResult result = await server.CallToolAsync(mcpTool.Name, parameters);
            
                if (result.IsError)
                {
                    logger.LogError($"❌ MCP Tool FAILED: {mcpTool.Name}");
                    logger.LogError($"   Error: {result.ErrorMessage}");
                    throw new InvalidOperationException($"Tool '{mcpTool.Name}' failed: {result.ErrorMessage}");
                }
            
                logger.LogInformation($"✅ MCP Tool SUCCESS: {mcpTool.Name}");
                logger.LogInformation($"   Result length: {result.Content?.Length ?? 0} chars");
            
                return result.Content ?? string.Empty;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"💥 MCP Tool EXCEPTION: {mcpTool.Name}");
                throw;
            }
        };
        
        // Build parameter descriptions for AIFunction metadata
        Dictionary<string, object> parameterDescriptions = [];
        
        foreach (KeyValuePair<string, Records.MCPParameterSchema> param in mcpTool.InputSchema.Properties)
        {
            string description = param.Value.Description;
            
            // Enhance description with type info and constraints
            if (param.Value.Enum != null && param.Value.Enum.Any())
            {
                description += $" (valori: {string.Join(", ", param.Value.Enum)})";
            }
            
            if (param.Value.Default != null)
            {
                description += $" (default: {param.Value.Default})";
            }
            
            parameterDescriptions[param.Key] = description;
        }
        
        // Create AIFunction with metadata
        AIFunction aiFunction = AIFunctionFactory.Create(
            implementation,
            new AIFunctionFactoryOptions
            {
                Name = mcpTool.Name,
                Description = mcpTool.Description,
                AdditionalProperties = new AdditionalPropertiesDictionary(parameterDescriptions)
            });
        
        logger.LogInformation($"Converted MCP tool to AIFunction: {mcpTool.Name}");
        Console.WriteLine($"Converted MCP tool to AIFunction: {mcpTool.Name}");
        return aiFunction;
    }
}