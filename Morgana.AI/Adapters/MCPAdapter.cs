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
    /// </summary>
    /// <param name="mcpTools">List of MCP tools from server</param>
    /// <returns>Dictionary mapping tool names to (delegate, definition) tuples</returns>
    public Dictionary<string, (Delegate toolDelegate, ToolDefinition toolDefinition)> ConvertTools(
        List<Tool> mcpTools)
    {
        Dictionary<string, (Delegate, ToolDefinition)> result = [];

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

                // Create delegate based on parameter count
                Delegate toolDelegate;
                
                if (parameters.Count == 0)
                {
                    // No parameters - use Func<Task<object>>
                    toolDelegate = new Func<Task<object>>(async () =>
                    {
                        try
                        {
                            logger.LogDebug($"Executing MCP tool (no params): {mcpTool.Name}");

                            CallToolResult callToolResult = await mcpClient.CallToolAsync(mcpTool.Name, null);

                            return FormatMCPResult(callToolResult);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, $"Error executing MCP tool: {mcpTool.Name}");
                            return $"Error: {ex.Message}";
                        }
                    });
                }
                else
                {
                    // Has parameters - use Func<Dictionary<string, object>, Task<object>>
                    toolDelegate = new Func<Dictionary<string, object>, Task<object>>(async (args) =>
                    {
                        try
                        {
                            logger.LogDebug($"Executing MCP tool: {mcpTool.Name}");

                            CallToolResult callToolResult = await mcpClient.CallToolAsync(mcpTool.Name, args);

                            return FormatMCPResult(callToolResult);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, $"Error executing MCP tool: {mcpTool.Name}");
                            return $"Error: {ex.Message}";
                        }
                    });
                }

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
                if (mcpTool.InputSchema.TryGetProperty("required", out JsonElement requiredElement) &&
                    requiredElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement req in requiredElement.EnumerateArray())
                    {
                        if (req.ValueKind == JsonValueKind.String)
                        {
                            requiredFields.Add(req.GetString()!);
                        }
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