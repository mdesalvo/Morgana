using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Servers;

/// <summary>
/// Implements IMCPServer for local/in-process tool providers.
/// Provides common infrastructure including error handling and logging.
/// </summary>
public abstract class MorganaInProcessMCPServer : IMCPServer
{
    protected readonly ILogger logger;
    protected readonly Records.MCPServerConfig config;
    protected readonly IConfiguration? configuration;

    public string ServerName => config.Name;

    protected MorganaInProcessMCPServer(
        Records.MCPServerConfig config,
        ILogger logger,
        IConfiguration? configuration = null)
    {
        this.config = config;
        this.logger = logger;
        this.configuration = configuration;
    }

    /// <summary>
    /// Normalizes parameter keys to handle LLM variations (snake_case, camelCase, etc.).
    /// LLMs often convert camelCase parameters to snake_case, especially in Italian contexts.
    /// This method creates a case-insensitive lookup and handles common transformations.
    /// </summary>
    /// <param name="parameters">Raw parameters from LLM</param>
    /// <param name="expectedKey">Expected parameter key (camelCase)</param>
    /// <param name="value">Normalized parameter value</param>
    /// <returns>True if parameter found (with any casing/format)</returns>
    protected bool TryGetNormalizedParameter(
        Dictionary<string, object> parameters,
        string expectedKey,
        out object? value)
    {
        // Try exact match first (fast path)
        if (parameters.TryGetValue(expectedKey, out value))
        {
            logger.LogDebug($"Parameter '{expectedKey}' found with exact match");
            return true;
        }

        // Try case-insensitive match
        foreach (KeyValuePair<string, object> kvp in parameters)
        {
            if (string.Equals(kvp.Key, expectedKey, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                logger.LogDebug($"Parameter '{expectedKey}' found with different casing: '{kvp.Key}' (value: {value ?? "NULL"})");
                return true;
            }
        }

        // Try snake_case → camelCase transformation
        // Example: "nome_prodotto" → "nomeProdotto"
        string snakeCaseKey = ToSnakeCase(expectedKey);
        if (parameters.TryGetValue(snakeCaseKey, out value))
        {
            logger.LogDebug($"Parameter '{expectedKey}' found as snake_case: '{snakeCaseKey}' (value: {value ?? "NULL"})");
            return true;
        }

        // Try removing underscores and case-insensitive
        // Example: "nomeprodotto" → "nomeProdotto"
        foreach (KeyValuePair<string, object> kvp in parameters)
        {
            string keyWithoutUnderscores = kvp.Key.Replace("_", "");
            string expectedWithoutUnderscores = expectedKey.Replace("_", "");
            if (string.Equals(keyWithoutUnderscores, expectedWithoutUnderscores, StringComparison.OrdinalIgnoreCase))
            {
                value = kvp.Value;
                logger.LogDebug($"Parameter '{expectedKey}' found with underscores removed: '{kvp.Key}' (value: {value ?? "NULL"})");
                return true;
            }
        }

        // Try bidirectional substring match (for truncated/partial parameters)
        // Example: LLM sends "nome" when expecting "nomeProdotto" (prefix)
        // Example: LLM sends "prodotto" when expecting "nomeProdotto" (suffix)
        // PROTECTION: Read from configuration or use defaults
        int minSubstringLength = configuration?.GetValue<int>("LLM:ParameterNormalization:MinSubstringLength") ?? 4;
        double similarityRatioThreshold = configuration?.GetValue<double>("LLM:ParameterNormalization:SimilarityRatioThreshold") ?? 0.3;
        List<KeyValuePair<string, object>> substringMatches = parameters
            .Where(p => p.Key.Length >= minSubstringLength)  // Skip very short keys
            .Where(p => 
                expectedKey.Contains(p.Key, StringComparison.OrdinalIgnoreCase) ||
                p.Key.Contains(expectedKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (substringMatches.Count == 1)
        {
            // Additional check: ensure the match is "significant" 
            // (matched string is at least X% of expected key length, configurable)
            string matchedKey = substringMatches[0].Key;
            double similarityRatio = (double)matchedKey.Length / expectedKey.Length;
            if (similarityRatio >= similarityRatioThreshold)
            {
                value = substringMatches[0].Value;
                logger.LogWarning($"Parameter '{expectedKey}' matched by substring with '{matchedKey}' (similarity: {similarityRatio:P0}, threshold: {similarityRatioThreshold:P0}, value: {value ?? "NULL"}). LLM may have sent partial parameter name.");
                return true;
            }
            logger.LogDebug($"Parameter '{matchedKey}' rejected as match for '{expectedKey}' (similarity {similarityRatio:P0} < threshold {similarityRatioThreshold:P0})");
        }
        else if (substringMatches.Count > 1)
        {
            // Multiple matches - ambiguous, log all candidates
            logger.LogError($"Parameter '{expectedKey}' has multiple substring matches: {string.Join(", ", substringMatches.Select(m => m.Key))}. Cannot determine which one to use.");
        }

        value = null;
        logger.LogWarning($"Parameter '{expectedKey}' NOT FOUND. Available parameters: {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value ?? "NULL"}"))}");
        return false;
    }

    /// <summary>
    /// Converts camelCase to snake_case.
    /// Example: "nomeProdotto" → "nome_prodotto"
    /// </summary>
    private static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        System.Text.StringBuilder result = new System.Text.StringBuilder();
        result.Append(char.ToLowerInvariant(input[0]));

        for (int i = 1; i < input.Length; i++)
        {
            char c = input[i];
            if (char.IsUpper(c))
            {
                result.Append('_');
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }
    
    /// <summary>
    /// Register available tools - implement in derived class.
    /// Define tool schemas with parameters and descriptions.
    /// </summary>
    /// <returns>Collection of tool definitions</returns>
    protected abstract Task<IEnumerable<Records.MCPToolDefinition>> RegisterToolsAsync();
    
    /// <summary>
    /// Execute specific tool - implement in derived class.
    /// Perform actual tool logic and return results.
    /// </summary>
    /// <param name="toolName">Tool identifier</param>
    /// <param name="parameters">Tool input parameters</param>
    /// <returns>Tool execution result</returns>
    protected abstract Task<Records.MCPToolResult> ExecuteToolAsync(
        string toolName, 
        Dictionary<string, object> parameters);
    
    // IMCPServer implementation with error handling
    
    public async Task<IEnumerable<Records.MCPToolDefinition>> ListToolsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation($"Listing tools from MCP server: {ServerName}");

            IEnumerable<Records.MCPToolDefinition> tools = await RegisterToolsAsync();

            logger.LogInformation($"Found {tools.Count()} tools in server: {ServerName}");

            return tools;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error listing tools from MCP server: {ServerName}");
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
            logger.LogInformation($"🎯 CallToolAsync: {toolName}");
            logger.LogInformation($"   Parameters: {string.Join(", ", parameters.Select(p => $"{p.Key}={p.Value}"))}");
        
            Records.MCPToolResult result = await ExecuteToolAsync(toolName, parameters);
        
            logger.LogInformation($"   Result: IsError={result.IsError}, ContentLength={result.Content?.Length ?? 0}");
        
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Exception in CallToolAsync for {toolName}");
            return new Records.MCPToolResult(true, null, $"Exception: {ex.Message}");
        }
    }
    
    public virtual Task<Records.MCPServerInfo> GetServerInfoAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new Records.MCPServerInfo(
            Name: ServerName,
            Version: "1.0.0",
            Capabilities: ["tools"]));
    }
}