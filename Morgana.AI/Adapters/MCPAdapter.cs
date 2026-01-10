using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
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
                // Log raw InputSchema for debugging
                logger.LogInformation($"[MCP SCHEMA] Tool: {mcpTool.Name}");
                logger.LogInformation($"[MCP SCHEMA]   Description: {mcpTool.Description}");
                logger.LogInformation($"[MCP SCHEMA]   InputSchema JSON: {mcpTool.InputSchema}");
                
                // Extract parameters from JSON Schema
                List<ToolParameter> parameters = ExtractParameters(mcpTool);

                // Log extracted parameters
                logger.LogInformation($"[MCP SCHEMA]   Extracted {parameters.Count} parameter(s):");
                foreach (var param in parameters)
                {
                    logger.LogInformation($"[MCP SCHEMA]     - {param.Name} (required: {param.Required})");
                }

                // Create tool definition
                ToolDefinition definition = new ToolDefinition(
                    Name: mcpTool.Name,
                    Description: mcpTool.Description ?? "No description available",
                    Parameters: parameters
                );

                // Create delegate that calls MCP server
                // Use dynamic delegate creation to match parameter names
                Delegate toolDelegate = CreateMCPToolDelegate(mcpTool, parameters);

                // Log delegate signature
                var methodInfo = toolDelegate.Method;
                var delegateParams = methodInfo.GetParameters();
                logger.LogInformation($"[MCP DELEGATE] Created delegate for {mcpTool.Name}:");
                logger.LogInformation($"[MCP DELEGATE]   Delegate type: {toolDelegate.GetType().Name}");
                logger.LogInformation($"[MCP DELEGATE]   Method has {delegateParams.Length} parameter(s):");
                foreach (var p in delegateParams)
                {
                    logger.LogInformation($"[MCP DELEGATE]     - {p.Name} ({p.ParameterType.Name})");
                }

                result[mcpTool.Name] = (toolDelegate, definition);
                logger.LogInformation($"[MCP SUCCESS] Converted tool: {mcpTool.Name}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"[MCP ERROR] Failed to convert MCP tool: {mcpTool.Name}");
            }
        }

        return result;
    }

    /// <summary>
    /// Creates a delegate for an MCP tool.
    /// Uses Expression Trees to ensure parameter names match the tool definition exactly.
    /// The patched ToolAdapter will filter out any Closure parameters that .NET adds.
    /// </summary>
    private Delegate CreateMCPToolDelegate(Tool mcpTool, List<ToolParameter> parameters)
    {
        string toolName = mcpTool.Name;
        string[] paramNames = parameters.Select(p => p.Name).ToArray();
        
        logger.LogInformation($"[DELEGATE BUILD] Creating delegate for {toolName} with {parameters.Count} parameter(s)");
        
        // For 0 parameters, use simple lambda
        if (parameters.Count == 0)
        {
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
        
        // For parameters > 0, use Expression Trees to set correct parameter names
        // Create the executor lambda with generic parameter names
        Delegate executor = parameters.Count switch
        {
            1 => new Func<string, Task<object>>(async (p0) =>
            {
                try
                {
                    logger.LogDebug($"Executing MCP tool (1 param): {toolName}");
                    Dictionary<string, object> args = new() { [paramNames[0]] = p0 };
                    CallToolResult result = await mcpClient.CallToolAsync(toolName, args);
                    return FormatMCPResult(result);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error executing MCP tool: {toolName}");
                    return $"Error: {ex.Message}";
                }
            }),
            
            2 => new Func<string, string, Task<object>>(async (p0, p1) =>
            {
                try
                {
                    logger.LogDebug($"Executing MCP tool (2 params): {toolName}");
                    Dictionary<string, object> args = new() 
                    { 
                        [paramNames[0]] = p0,
                        [paramNames[1]] = p1
                    };
                    CallToolResult result = await mcpClient.CallToolAsync(toolName, args);
                    return FormatMCPResult(result);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error executing MCP tool: {toolName}");
                    return $"Error: {ex.Message}";
                }
            }),
            
            3 => new Func<string, string, string, Task<object>>(async (p0, p1, p2) =>
            {
                try
                {
                    logger.LogDebug($"Executing MCP tool (3 params): {toolName}");
                    Dictionary<string, object> args = new() 
                    { 
                        [paramNames[0]] = p0,
                        [paramNames[1]] = p1,
                        [paramNames[2]] = p2
                    };
                    CallToolResult result = await mcpClient.CallToolAsync(toolName, args);
                    return FormatMCPResult(result);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Error executing MCP tool: {toolName}");
                    return $"Error: {ex.Message}";
                }
            }),
            
            _ => throw new NotSupportedException(
                $"MCP tools with {parameters.Count} parameters not supported. Max 3 currently supported.")
        };
        
        // Now wrap the executor in an Expression Tree to set correct parameter names
        return CreateDelegateWithNamedParameters(executor, paramNames);
    }
    
    /// <summary>
    /// Wraps an existing delegate using DynamicMethod to create a method with named parameters.
    /// This is necessary because AIFunctionFactory requires parameter names.
    /// </summary>
    private Delegate CreateDelegateWithNamedParameters(Delegate executor, string[] paramNames)
    {
        // Build delegate type
        Type[] paramTypes = Enumerable.Repeat(typeof(string), paramNames.Length).ToArray();
        Type returnType = typeof(Task<object>);
        Type[] allTypes = paramTypes.Concat([returnType]).ToArray();
        Type delegateType = Expression.GetFuncType(allTypes);
        
        // Create DynamicMethod
        DynamicMethod dynamicMethod = new DynamicMethod(
            $"MCP_Wrapper_{Guid.NewGuid():N}",
            returnType,
            paramTypes,
            typeof(MCPAdapter).Module,
            skipVisibility: true);
        
        // Define parameter names
        for (int i = 0; i < paramNames.Length; i++)
        {
            dynamicMethod.DefineParameter(i + 1, ParameterAttributes.None, paramNames[i]);
        }
        
        // Store executor in a static field so we can reference it in IL
        string fieldKey = Guid.NewGuid().ToString();
        lock (executorCache)
        {
            executorCache[fieldKey] = executor;
        }
        
        // Generate IL
        ILGenerator il = dynamicMethod.GetILGenerator();
        
        // Load the executor from cache
        il.Emit(OpCodes.Ldstr, fieldKey);
        il.Emit(OpCodes.Call, typeof(MCPAdapter).GetMethod(nameof(GetExecutorFromCache), 
            BindingFlags.NonPublic | BindingFlags.Static)!);
        
        // Load all parameters onto the stack
        for (int i = 0; i < paramNames.Length; i++)
        {
            il.Emit(OpCodes.Ldarg, i);
        }
        
        // Call executor.Invoke with parameters
        MethodInfo invokeMethod = executor.GetType().GetMethod("Invoke")!;
        il.Emit(OpCodes.Callvirt, invokeMethod);
        
        // Return
        il.Emit(OpCodes.Ret);
        
        // Create delegate from DynamicMethod
        return dynamicMethod.CreateDelegate(delegateType);
    }
    
    /// <summary>
    /// Static cache for executor delegates referenced by DynamicMethod IL.
    /// </summary>
    private static readonly Dictionary<string, Delegate> executorCache = [];
    
    /// <summary>
    /// Helper method called by DynamicMethod IL to retrieve executor from cache.
    /// </summary>
    private static Delegate GetExecutorFromCache(string key)
    {
        lock (executorCache)
        {
            return executorCache.TryGetValue(key, out Delegate? executor)
                ? executor
                : throw new InvalidOperationException($"Executor '{key}' not found in cache");
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