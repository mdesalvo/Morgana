using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Adapters;

/// <summary>
/// Adapter for remote HTTP MCP servers.
/// Implements IMCPServer by forwarding calls to remote HTTP endpoints.
/// Provides graceful degradation if remote server is unreachable.
/// </summary>
public class HttpMCPServerAdapter : IMCPServer
{
    private readonly Records.MCPServerConfig config;
    private readonly ILogger<HttpMCPServerAdapter> logger;
    private readonly HttpClient httpClient;
    private readonly string baseUrl;
    
    public string ServerName => config.Name;
    
    public HttpMCPServerAdapter(
        Records.MCPServerConfig config,
        ILogger<HttpMCPServerAdapter> logger,
        IHttpClientFactory httpClientFactory)
    {
        this.config = config;
        this.logger = logger;
        
        // Get endpoint from AdditionalSettings (required for HTTP servers)
        if (config.AdditionalSettings?.TryGetValue("Endpoint", out string? endpoint) != true || string.IsNullOrEmpty(endpoint))
            throw new InvalidOperationException($"HTTP MCP server '{config.Name}' requires 'Endpoint' in AdditionalSettings");

        baseUrl = endpoint;
        
        // Create HTTP client with server-specific configuration
        httpClient = httpClientFactory.CreateClient(config.Name);
        httpClient.BaseAddress = new Uri(baseUrl);
        
        // Configure timeout from settings or use default
        if (config.AdditionalSettings?.TryGetValue("TimeoutSeconds", out string? timeoutStr) == true 
            && int.TryParse(timeoutStr, out int timeout))
        {
            httpClient.Timeout = TimeSpan.FromSeconds(timeout);
        }
        else
        {
            httpClient.Timeout = TimeSpan.FromSeconds(30); // Default 30s
        }
        
        // Configure authentication if provided
        ConfigureAuthentication();
        
        logger.LogInformation($"HttpMCPServerAdapter initialized for '{ServerName}' at {baseUrl}");
    }
    
    private void ConfigureAuthentication()
    {
        if (config.AdditionalSettings == null) return;
        
        // API Key authentication
        if (config.AdditionalSettings.TryGetValue("ApiKey", out string? apiKey))
        {
            httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);
            logger.LogDebug($"Configured API Key authentication for {ServerName}");
        }
        
        // Bearer token authentication
        if (config.AdditionalSettings.TryGetValue("BearerToken", out string? token))
        {
            httpClient.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Bearer", token);
            logger.LogDebug($"Configured Bearer token authentication for {ServerName}");
        }
        
        // Basic authentication
        if (config.AdditionalSettings.TryGetValue("Username", out string? username) &&
            config.AdditionalSettings.TryGetValue("Password", out string? password))
        {
            string credentials = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes($"{username}:{password}"));
            httpClient.DefaultRequestHeaders.Authorization = 
                new AuthenticationHeaderValue("Basic", credentials);
            logger.LogDebug($"Configured Basic authentication for {ServerName}");
        }
    }
    
    public async Task<IEnumerable<Records.MCPToolDefinition>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation($"Discovering tools from remote MCP server: {ServerName}");
            
            HttpResponseMessage response = await httpClient.GetAsync(
                "mcp/tools", 
                cancellationToken);
            
            response.EnsureSuccessStatusCode();
            
            Records.MCPToolDefinition[]? tools = await response.Content
                .ReadFromJsonAsync<Records.MCPToolDefinition[]>(cancellationToken);
            
            logger.LogInformation($"✅ Discovered {tools?.Length ?? 0} tools from remote server '{ServerName}'");
            
            return tools ?? [];
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, 
                $"❌ Remote MCP server '{ServerName}' unreachable at {baseUrl}. " +
                $"Agent will operate without these tools.");
            return [];
        }
        catch (TaskCanceledException ex)
        {
            logger.LogError(ex, 
                $"⏱️ Remote MCP server '{ServerName}' timed out at {baseUrl}. " +
                $"Agent will operate without these tools.");
            return [];
        }
        catch (Exception ex)
        {
            logger.LogError(ex, 
                $"💥 Unexpected error discovering tools from remote server '{ServerName}'. " +
                $"Agent will operate without these tools.");
            return [];
        }
    }
    
    public async Task<Records.MCPToolResult> CallToolAsync(
        string toolName,
        Dictionary<string, object> parameters,
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation($"🔧 Invoking remote tool '{toolName}' on server '{ServerName}'");
            
            var request = new
            {
                toolName,
                parameters
            };
            
            HttpResponseMessage response = await httpClient.PostAsJsonAsync(
                $"mcp/tools/{toolName}",
                request,
                cancellationToken);
            
            response.EnsureSuccessStatusCode();
            
            Records.MCPToolResult? result = await response.Content
                .ReadFromJsonAsync<Records.MCPToolResult>(cancellationToken);
            
            if (result == null)
            {
                logger.LogError($"Remote server '{ServerName}' returned null result for tool '{toolName}'");
                return new Records.MCPToolResult(
                    IsError: true,
                    Content: null,
                    ErrorMessage: "Remote server returned invalid response");
            }
            
            logger.LogInformation(
                $"✅ Remote tool '{toolName}' completed successfully (IsError: {result.IsError})");
            
            return result;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, $"❌ Remote tool '{toolName}' invocation failed on '{ServerName}'");
            return new Records.MCPToolResult(
                IsError: true,
                Content: null,
                ErrorMessage: $"Remote invocation failed: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            logger.LogError(ex, $"⏱️ Remote tool '{toolName}' timed out on '{ServerName}'");
            return new Records.MCPToolResult(
                IsError: true,
                Content: null,
                ErrorMessage: $"Remote invocation timed out: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"💥 Unexpected error invoking remote tool '{toolName}' on '{ServerName}'");
            return new Records.MCPToolResult(
                IsError: true,
                Content: null,
                ErrorMessage: $"Unexpected error: {ex.Message}");
        }
    }
    
    public Task<Records.MCPServerInfo> GetServerInfoAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Records.MCPServerInfo(
            Name: ServerName,
            Version: "Remote-HTTP",
            Capabilities: ["tools", "http"]));
    }
}