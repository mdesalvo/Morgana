using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Controllers;

/// <summary>
/// ASP.NET Controller that exposes MCP servers via HTTP endpoints.
/// Allows remote Morgana instances or third-party clients to consume InProcess MCP servers.
/// </summary>
[ApiController]
[Route("mcp")]
public class MorganaMCPController : ControllerBase
{
    private readonly IEnumerable<IMCPServer> mcpServers;
    private readonly ILogger logger;
    
    public MorganaMCPController(
        IEnumerable<IMCPServer> mcpServers,
        ILogger logger)
    {
        this.mcpServers = mcpServers;
        this.logger = logger;
    }
    
    /// <summary>
    /// GET /mcp/tools
    /// Lists all available tools from all MCP servers.
    /// Query parameter 'serverName' can filter by specific server.
    /// </summary>
    [HttpGet("tools")]
    [ProducesResponseType(typeof(IEnumerable<Records.MCPToolDefinition>), 200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> ListTools([FromQuery] string? serverName = null)
    {
        logger.LogInformation($"📋 Listing MCP tools (serverName: {serverName ?? "all"})");
        
        if (!string.IsNullOrEmpty(serverName))
        {
            // Filter by specific server
            IMCPServer? server = mcpServers.FirstOrDefault(s => 
                string.Equals(s.ServerName, serverName, StringComparison.OrdinalIgnoreCase));
            
            if (server == null)
            {
                logger.LogWarning($"❌ MCP server '{serverName}' not found");
                return NotFound(new { error = $"MCP server '{serverName}' not found" });
            }
            
            IEnumerable<Records.MCPToolDefinition> tools = await server.ListToolsAsync();
            
            logger.LogInformation($"✅ Returning {tools.Count()} tools from server '{serverName}'");
            return Ok(tools);
        }
        
        // Return all tools from all servers
        List<Records.MCPToolDefinition> allTools = [];
        
        foreach (IMCPServer server in mcpServers)
        {
            IEnumerable<Records.MCPToolDefinition> tools = await server.ListToolsAsync();
            allTools.AddRange(tools);
        }
        
        logger.LogInformation($"✅ Returning {allTools.Count} tools from {mcpServers.Count()} servers");
        return Ok(allTools);
    }
    
    /// <summary>
    /// POST /mcp/tools/{toolName}
    /// Invokes a specific tool on a specific MCP server.
    /// Request body must contain: { "serverName": "...", "parameters": {...} }
    /// </summary>
    [HttpPost("tools/{toolName}")]
    [ProducesResponseType(typeof(Records.MCPToolResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> InvokeTool(
        string toolName,
        [FromBody] Records.InvokeToolRequest request)
    {
        logger.LogInformation($"🔧 Invoking tool '{toolName}' on server '{request.ServerName}'");
        
        if (string.IsNullOrEmpty(request.ServerName))
        {
            logger.LogWarning("❌ Missing serverName in request");
            return BadRequest(new { error = "serverName is required" });
        }
        
        // Find the target server
        IMCPServer? server = mcpServers.FirstOrDefault(s => 
            string.Equals(s.ServerName, request.ServerName, StringComparison.OrdinalIgnoreCase));
        
        if (server == null)
        {
            logger.LogWarning($"❌ MCP server '{request.ServerName}' not found");
            return NotFound(new { error = $"MCP server '{request.ServerName}' not found" });
        }
        
        // Invoke the tool
        Records.MCPToolResult result = await server.CallToolAsync(
            toolName, 
            request.Parameters ?? new Dictionary<string, object>());
        
        if (result.IsError)
        {
            logger.LogError($"❌ Tool '{toolName}' returned error: {result.ErrorMessage}");
            return BadRequest(result);
        }
        
        logger.LogInformation($"✅ Tool '{toolName}' executed successfully");
        return Ok(result);
    }
    
    /// <summary>
    /// GET /mcp/servers
    /// Lists all available MCP servers with their metadata.
    /// </summary>
    [HttpGet("servers")]
    [ProducesResponseType(typeof(IEnumerable<Records.MCPServerInfo>), 200)]
    public async Task<IActionResult> ListServers()
    {
        logger.LogInformation("📋 Listing MCP servers");
        
        List<Records.MCPServerInfo> serverInfos = [];
        
        foreach (IMCPServer server in mcpServers)
        {
            Records.MCPServerInfo info = await server.GetServerInfoAsync();
            serverInfos.Add(info);
        }
        
        logger.LogInformation($"✅ Returning {serverInfos.Count} MCP servers");
        return Ok(serverInfos);
    }
}