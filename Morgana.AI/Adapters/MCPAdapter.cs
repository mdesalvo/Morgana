using System.Linq.Expressions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using static Morgana.AI.Records;

namespace Morgana.AI.Adapters;

/// <summary>
/// Adapter for converting MCP tools to Morgana ToolDefinition format.
/// Creates delegates that bridge MCP tool calls to the MCP server.
/// </summary>
public class MCPAdapter
{
    private readonly MCPClient mcpClient;
    private readonly ILogger logger;

    public MCPAdapter(MCPClient mcpClient, ILogger logger)
    {
        this.mcpClient = mcpClient;
        this.logger = logger;
    }

    /// <summary>
    /// Converts MCP tools to Morgana tool definitions with execution delegates.
    /// Creates delegates with proper parameter signatures to match ToolAdapter validation.
    /// </summary>
    /// <param name="mcpTools">List of MCP tools from server</param>
    /// <returns>Dictionary mapping tool names to (delegate, definition) tuples</returns>
    public Dictionary<string, (Delegate toolDelegate, ToolDefinition toolDefinition)> ConvertTools(
        List<Tool> mcpTools)
    {
        Dictionary<string, (Delegate, ToolDefinition)> result = new();

        foreach (Tool mcpTool in mcpTools)
        {
            try
            {
                // Extract parameters from JSON Schema
                List<ToolParameter> parameters = ExtractParameters(mcpTool);

                // Create tool definition
                ToolDefinition definition = new ToolDefinition(
                    Name: mcpTool.Name,
                    Description: mcpTool.Description ?? "No description available",
                    Parameters: parameters
                );

                // Create delegate that calls MCP server
                // Use dynamic delegate creation to match parameter names
                Delegate toolDelegate = CreateMCPToolDelegate(mcpTool, parameters);

                result[mcpTool.Name] = (toolDelegate, definition);
                logger.LogDebug($"Converted MCP tool: {mcpTool.Name} ({parameters.Count} parameters)");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to convert MCP tool: {mcpTool.Name}");
            }
        }

        return result;
    }

    /// <summary>
    /// Creates a delegate for an MCP tool that matches the exact parameter signature.
    /// Uses dynamic method creation to ensure parameter names match ToolDefinition.
    /// </summary>
    private Delegate CreateMCPToolDelegate(Tool mcpTool, List<ToolParameter> parameters)
    {
        if (parameters.Count == 0)
        {
            // No parameters - simple delegate
            return new Func<Task<object>>(async () =>
            {
                try
                {
                    logger.LogDebug($"Executing MCP tool (no params): {mcpTool.Name}");

                    CallToolResult result = await mcpClient.CallToolAsync(mcpTool.Name, null);

                    return FormatMCPResult(result);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error executing MCP tool: {mcpTool.Name}");
                    return $"Error: {ex.Message}";
                }
            });
        }

        // For tools with parameters, we need to create a dynamic delegate
        // that accepts individual parameters (not Dictionary<string, object>)
        // We'll use reflection to build the right delegate type and parameter names
        
        // Build parameter types array (all parameters are strings or objects)
        Type[] parameterTypes = parameters.Select(_ => typeof(string)).ToArray();
        Type returnType = typeof(Task<object>);
        
        // Create delegate type: Func<string, string, ..., Task<object>>
        Type[] allTypes = parameterTypes.Concat([returnType]).ToArray();
        Type delegateType = Expression.GetFuncType(allTypes);
        
        // Create lambda expression that calls MCP
        ParameterExpression[] paramExprs = parameters
            .Select(p => Expression.Parameter(typeof(string), p.Name))
            .ToArray();
        
        // Build method call expression
        LambdaExpression callExpression = Expression.Lambda(
            delegateType,
            Expression.Call(
                Expression.Constant(this),
                typeof(MCPAdapter).GetMethod(nameof(CallMCPToolAsync), 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!,
                Expression.Constant(mcpTool.Name),
                Expression.NewArrayInit(
                    typeof(string),
                    paramExprs.Cast<Expression>()
                ),
                Expression.Constant(parameters.Select(p => p.Name).ToArray())
            ),
            paramExprs
        );
        
        return callExpression.Compile();
    }

    /// <summary>
    /// Helper method called by dynamically created delegates.
    /// Converts parameter array to dictionary and calls MCP server.
    /// </summary>
    private async Task<object> CallMCPToolAsync(string toolName, string[] paramValues, string[] paramNames)
    {
        try
        {
            logger.LogDebug($"Executing MCP tool: {toolName}");
            
            // Build arguments dictionary
            Dictionary<string, object> args = [];
            for (int i = 0; i < paramNames.Length; i++)
                args[paramNames[i]] = paramValues[i];
            
            CallToolResult result = await mcpClient.CallToolAsync(toolName, args);

            return FormatMCPResult(result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error executing MCP tool: {toolName}");
            return $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Extracts parameters from MCP tool InputSchema (JSON Schema format).
    /// </summary>
    private List<ToolParameter> ExtractParameters(Tool mcpTool)
    {
        List<ToolParameter> parameters = [];

        // InputSchema is JsonElement (struct), check if it has properties
        if (mcpTool.InputSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return parameters;

        try
        {
            // Parse properties
            if (mcpTool.InputSchema.TryGetProperty("properties", out JsonElement propertiesElement))
            {
                // Parse required fields
                HashSet<string> requiredFields = [];
                if (mcpTool.InputSchema.TryGetProperty("required", out JsonElement requiredElement)
                     && requiredElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement req in requiredElement.EnumerateArray())
                    {
                        if (req.ValueKind == JsonValueKind.String)
                            requiredFields.Add(req.GetString()!);
                    }
                }

                // Extract each parameter
                foreach (JsonProperty property in propertiesElement.EnumerateObject())
                {
                    string paramName = property.Name;
                    JsonElement paramSchema = property.Value;

                    string description = "No description";
                    if (paramSchema.TryGetProperty("description", out JsonElement descElement))
                        description = descElement.GetString() ?? description;

                    bool isRequired = requiredFields.Contains(paramName);

                    parameters.Add(new ToolParameter(
                        Name: paramName,
                        Description: description,
                        Required: isRequired,
                        Scope: "request", // MCP parameters are always request-scoped
                        Shared: false
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Error parsing InputSchema for tool: {mcpTool.Name}");
        }

        return parameters;
    }

    /// <summary>
    /// Formats MCP CallToolResult into a string suitable for LLM consumption.
    /// </summary>
    private string FormatMCPResult(CallToolResult result)
    {
        if (result.Content == null || result.Content.Count == 0)
        {
            return "Tool executed successfully (no content returned)";
        }

        List<string> formattedParts = [];

        foreach (ContentBlock content in result.Content)
        {
            switch (content)
            {
                case TextContentBlock textContent:
                    formattedParts.Add(textContent.Text);
                    break;
                case ImageContentBlock imageContent:
                    formattedParts.Add($"[Image returned: {imageContent.MimeType}]");
                    break;
                case AudioContentBlock audioContent:
                    formattedParts.Add($"[Audio returned: {audioContent.MimeType}]");
                    break;
                case EmbeddedResourceBlock embeddedResource:
                    formattedParts.Add($"[Resource: {embeddedResource.Resource.Uri}]");
                    break;
                case ResourceLinkBlock resourceLink:
                    formattedParts.Add($"[Resource link: {resourceLink.Uri}]");
                    break;
                default:
                    formattedParts.Add($"[Unknown content type: {content.GetType().Name}]");
                    break;
            }
        }

        return string.Join("\n", formattedParts);
    }
}