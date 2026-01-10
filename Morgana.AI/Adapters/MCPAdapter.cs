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
    /// Uses Expression Trees to ensure parameter names match ToolDefinition exactly.
    /// </summary>
    private Delegate CreateMCPToolDelegate(Tool mcpTool, List<ToolParameter> parameters)
    {
        string toolName = mcpTool.Name;
        
        if (parameters.Count == 0)
        {
            // No parameters - simple delegate
            return new Func<Task<object>>(async () =>
            {
                try
                {
                    logger.LogDebug($"Executing MCP tool (0 params): {toolName}");

                    CallToolResult result = await mcpClient.CallToolAsync(toolName, null);

                    return FormatMCPResult(result);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error executing MCP tool: {toolName}");
                    return $"Error: {ex.Message}";
                }
            });
        }

        // For tools with parameters, use Expression Trees to set correct parameter names
        // This is required because ToolAdapter validates parameter names match
        
        // Create parameter expressions with exact names from ToolDefinition
        ParameterExpression[] paramExprs = parameters
            .Select(p => Expression.Parameter(typeof(string), p.Name))
            .ToArray();
        
        // Create the method we'll call: a local helper that builds args and calls MCP
        Func<string[], Task<object>> executeFunc = async (args) =>
        {
            try
            {
                logger.LogDebug($"Executing MCP tool ({args.Length} params): {toolName}");
                
                // Build arguments dictionary
                Dictionary<string, object> argsDict = new();
                for (int i = 0; i < parameters.Count; i++)
                {
                    argsDict[parameters[i].Name] = args[i];
                }

                CallToolResult result = await mcpClient.CallToolAsync(toolName, argsDict);

                return FormatMCPResult(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Error executing MCP tool: {toolName}");
                return $"Error: {ex.Message}";
            }
        };
        
        // Create array of parameter values: new[] { p1, p2, p3, ... }
        NewArrayExpression argsArray = Expression.NewArrayInit(
            typeof(string),
            paramExprs.Cast<Expression>()
        );
        
        // Create call: executeFunc(new[] { p1, p2, ... })
        MethodCallExpression callExpr = Expression.Call(
            Expression.Constant(executeFunc.Target),
            executeFunc.Method,
            argsArray
        );
        
        // Build delegate type: Func<string, Task<object>> or Func<string, string, Task<object>>, etc.
        Type[] paramTypes = paramExprs.Select(p => p.Type).Concat([typeof(Task<object>)]).ToArray();
        Type delegateType = Expression.GetFuncType(paramTypes);
        
        // Create lambda: (name, journey_id, ...) => executeFunc(new[] { name, journey_id, ... })
        LambdaExpression lambda = Expression.Lambda(delegateType, callExpr, paramExprs);
        
        return lambda.Compile();
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