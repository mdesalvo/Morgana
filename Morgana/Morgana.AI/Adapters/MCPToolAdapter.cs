using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Morgana.AI.Services;

namespace Morgana.AI.Adapters;

/// <summary>
/// Adapter for converting MCP tools to Morgana ToolDefinition format.
/// Creates delegates that bridge MCP tool calls to the MCP server.
/// </summary>
public class MCPToolAdapter
{
    /// <summary>
    /// Connected client for the target MCP server. All tool invocations produced by this
    /// adapter are forwarded through it.
    /// </summary>
    private readonly MCPClient mcpClient;

    /// <summary>
    /// Logger used for adapter diagnostics: tool conversion, parameter extraction,
    /// and DynamicMethod IL generation.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Static cache for executor delegates referenced by DynamicMethod IL.
    /// </summary>
    /// <remarks>
    /// DynamicMethod cannot close over local variables the way a lambda can — it has no
    /// implicit closure object. The only way for IL code to reach a managed object at
    /// invocation time is to load it from a statically reachable location. This dictionary
    /// is that anchor: the IL embeds the lookup key as a string literal and calls
    /// <see cref="GetObjectArrayExecutorFromCache"/> to retrieve the executor at call time.
    ///
    /// Keys are deterministic (<c>"{serverLabel}:{toolName}"</c>) so the same compiled wiring
    /// is reused across all conversations that invoke the same tool on the same server.
    /// The cache is bounded: one entry per unique tool per MCP server for the process lifetime.
    /// </remarks>
    private static readonly Dictionary<string, object> executorCache = [];

    /// <summary>
    /// Internal record to track MCP parameter metadata including .NET types.
    /// This allows us to support typed parameters without modifying ToolParameter record.
    /// </summary>
    private record MCPParameterInfo(
        string Name,
        Type ClrType,
        string JsonType);

    /// <summary>
    /// Initializes the adapter with the MCP client used to forward tool invocations.
    /// </summary>
    /// <param name="mcpClient">Connected client for the target MCP server.</param>
    /// <param name="logger">Logger for adapter diagnostics.</param>
    public MCPToolAdapter(MCPClient mcpClient, ILogger logger)
    {
        this.mcpClient = mcpClient;
        this.logger = logger;
    }

    /// <summary>
    /// Converts MCP tools to Morgana tool definitions with execution delegates.
    /// Creates delegates with proper parameter signatures using DynamicMethod IL generation.
    /// Tracks parameter types internally to support typed parameters (int, bool, double, etc.)
    /// </summary>
    /// <param name="mcpTools">List of MCP tools from server</param>
    /// <returns>Dictionary mapping tool names to (delegate, definition) tuples</returns>
    public Dictionary<string, (Delegate toolDelegate, Records.ToolDefinition toolDefinition)> ConvertTools(List<Tool> mcpTools)
    {
        Dictionary<string, (Delegate, Records.ToolDefinition)> result = [];

        foreach (Tool mcpTool in mcpTools)
        {
            try
            {
                // Extract parameters with type information
                (List<Records.ToolParameter> toolParams, List<MCPParameterInfo> paramInfos) = ExtractParametersWithTypes(mcpTool);

                // Create tool definition (uses existing ToolParameter without Type field)
                Records.ToolDefinition definition = new Records.ToolDefinition(
                    Name: mcpTool.Name,
                    Description: mcpTool.Description ?? "No description available",
                    Parameters: toolParams);

                // Create delegate using parameter type information
                Delegate toolDelegate = CreateMCPToolDelegate(mcpTool, paramInfos);

                result[mcpTool.Name] = (toolDelegate, definition);

                logger.LogDebug("Converted MCP tool: {McpToolName} ({ParamInfosCount} parameters)", mcpTool.Name, paramInfos.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to convert MCP tool: {McpToolName}", mcpTool.Name);
            }
        }

        return result;
    }

    /// <summary>
    /// Creates a delegate for an MCP tool.
    /// Supports any number of parameters with proper typing through dynamic IL generation.
    /// </summary>
    private Delegate CreateMCPToolDelegate(Tool mcpTool, List<MCPParameterInfo> parameters)
    {
        // 0-parameter tools: a plain lambda is sufficient.
        // AIFunctionFactory only needs to reflect named parameters when there are parameters to
        // inspect; with an empty signature there is nothing to name, so no DynamicMethod is needed.
        if (parameters.Count == 0)
        {
            return new Func<Task<object>>(async () =>
            {
                try
                {
                    logger.LogDebug("Executing MCP tool (0 params): {McpToolName}", mcpTool.Name);

                    CallToolResult result = await mcpClient.CallToolAsync(mcpTool.Name, null);

                    return FormatMCPResult(result);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error executing MCP tool: {McpToolName}", mcpTool.Name);
                    return $"Error: {ex.Message}";
                }
            });
        }

        // For 1+ parameters, create executor that handles type conversion
        // The executor receives object[] to handle mixed types
        Func<object[], Task<object>> executor = async (values) =>
        {
            try
            {
                logger.LogDebug("Executing MCP tool ({ValuesLength} params): {McpToolName}", values.Length, mcpTool.Name);

                Dictionary<string, object> args = [];
                for (int i = 0; i < parameters.Count; i++)
                {
                    // Convert value to appropriate type for JSON serialization
                    args[parameters[i].Name] = ConvertValueForMCP(values[i], parameters[i]);
                }

                CallToolResult result = await mcpClient.CallToolAsync(mcpTool.Name, args);

                return FormatMCPResult(result);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error executing MCP tool: {McpToolName}", mcpTool.Name);
                return $"Error: {ex.Message}";
            }
        };

        // Wrap executor in DynamicMethod with proper parameter names and types
        return CreateTypedDelegateWithNamedParameters(executor, parameters, mcpTool.Name);
    }

    /// <summary>
    /// Coerces a runtime parameter value to the CLR type expected by the MCP server.
    /// </summary>
    /// <remarks>
    /// Values arriving from the LLM via <c>AIFunction</c> may be boxed strings even when
    /// the declared type is numeric or boolean, because the LLM produces JSON text that the
    /// framework deserialises loosely. Explicit coercion here ensures the MCP server receives
    /// the typed value it declared in its JSON Schema rather than an unexpected string.
    /// </remarks>
    private object ConvertValueForMCP(object value, MCPParameterInfo paramInfo)
    {
        return paramInfo.JsonType.ToLowerInvariant() switch
        {
            "boolean" => value is bool b ? b : bool.Parse(value?.ToString() ?? "false"),
            "integer" => value is int i ? i : int.Parse(value?.ToString() ?? "0"),
            "number" => value is double d ? d : double.Parse(value?.ToString() ?? "0"),
            _ => value?.ToString() ?? ""
        };
    }

    /// <summary>
    /// Wraps an object array executor in a <see cref="DynamicMethod"/> whose signature has
    /// the correct parameter names and CLR types required by <c>AIFunctionFactory</c>.
    /// </summary>
    /// <remarks>
    /// <c>AIFunctionFactory.Create</c> uses reflection to read parameter names from
    /// <c>MethodInfo.GetParameters()</c>. A plain <c>Func&lt;object[], Task&lt;object&gt;&gt;</c>
    /// exposes only a single anonymous <c>object[]</c> parameter, so the factory would not
    /// see the individual MCP parameter names.
    ///
    /// Expression.Lambda was evaluated as an alternative but encountered closure-capture issues
    /// at runtime with the way AIFunctionFactory inspects the compiled delegate; DynamicMethod
    /// with explicit <c>DefineParameter</c> calls is the reliable path.
    ///
    /// The executor itself cannot be embedded in the IL as a literal — IL only supports
    /// string/integer/type constants. It is stored in <see cref="executorCache"/> under a
    /// deterministic key and retrieved by the generated IL at each invocation.
    /// </remarks>
    private Delegate CreateTypedDelegateWithNamedParameters(Func<object[], Task<object>> executor, List<MCPParameterInfo> parameters, string toolName)
    {
        // Build delegate type: Func<T1, T2, ..., Task<object>> where T1, T2 are actual CLR types
        Type[] paramTypes = parameters.Select(p => p.ClrType).ToArray();
        Type returnType = typeof(Task<object>);
        Type[] allTypes = paramTypes.Concat([returnType]).ToArray();
        Type delegateType = Expression.GetFuncType(allTypes);

        // Create DynamicMethod
        DynamicMethod dynamicMethod = new DynamicMethod(
            $"MCP_Wrapper_{Guid.NewGuid():N}",
            returnType,
            paramTypes,
            typeof(MCPToolAdapter).Module,
            skipVisibility: true);

        // Define parameter names
        for (int i = 0; i < parameters.Count; i++)
            dynamicMethod.DefineParameter(i + 1, ParameterAttributes.None, parameters[i].Name);

        // Deterministic key: server URI + tool name uniquely identify the wiring object.
        // Prevents unbounded cache growth — one entry per tool per server, regardless of
        // how many conversations invoke the same tool.
        string fieldKey = $"{mcpClient.ServerLabel}:{toolName}";
        lock (executorCache)
        {
            executorCache.TryAdd(fieldKey, executor);
        }

        // Generate IL: the remote MCP tool is handled as an equivalent
        //              IL method generated once at runtime (and cached)
        ILGenerator il = dynamicMethod.GetILGenerator();

        // Load the executor from cache
        il.Emit(OpCodes.Ldstr, fieldKey);
        il.Emit(OpCodes.Call, typeof(MCPToolAdapter).GetMethod(nameof(GetObjectArrayExecutorFromCache),
            BindingFlags.NonPublic | BindingFlags.Static)!);

        // Create object array for parameters
        il.Emit(OpCodes.Ldc_I4, parameters.Count); // Array length
        il.Emit(OpCodes.Newarr, typeof(object));   // Create array

        // Populate array with parameters (box value types)
        for (int i = 0; i < parameters.Count; i++)
        {
            il.Emit(OpCodes.Dup);           // Duplicate array reference
            il.Emit(OpCodes.Ldc_I4, i);     // Array index
            il.Emit(OpCodes.Ldarg, i);      // Load parameter value

            // Box value types
            if (parameters[i].ClrType.IsValueType)
                il.Emit(OpCodes.Box, parameters[i].ClrType);

            il.Emit(OpCodes.Stelem_Ref);    // Store in array
        }

        // Call executor.Invoke(object[] args)
        MethodInfo invokeMethod = typeof(Func<object[], Task<object>>).GetMethod("Invoke")!;
        il.Emit(OpCodes.Callvirt, invokeMethod);

        // Return
        il.Emit(OpCodes.Ret);

        // Create delegate from DynamicMethod
        return dynamicMethod.CreateDelegate(delegateType);
    }

    /// <summary>
    /// Retrieves an executor from <see cref="executorCache"/> by key.
    /// Called exclusively from the IL emitted by <see cref="CreateTypedDelegateWithNamedParameters"/>,
    /// not from any C# call site — the method must remain non-private and statically reachable
    /// so the IL <c>call</c> opcode can resolve it.
    /// </summary>
    private static Func<object[], Task<object>> GetObjectArrayExecutorFromCache(string key)
    {
        lock (executorCache)
        {
            return executorCache.TryGetValue(key, out object? executor)
                ? (Func<object[], Task<object>>)executor
                : throw new InvalidOperationException($"Executor '{key}' not found in cache");
        }
    }


    /// <summary>
    /// Extracts parameters from MCP tool InputSchema (JSON Schema format).
    /// Returns both ToolParameter (for Morgana) and MCPParameterInfo (for type-aware delegate generation).
    /// </summary>
    private (List<Records.ToolParameter> toolParams, List<MCPParameterInfo> paramInfos) ExtractParametersWithTypes(Tool mcpTool)
    {
        List<Records.ToolParameter> toolParams = [];
        List<MCPParameterInfo> paramInfos = [];

        // JsonElement is a struct — it cannot be null. ValueKind is the correct way to
        // detect an absent or empty schema before attempting property enumeration.
        if (mcpTool.InputSchema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return (toolParams, paramInfos);

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

                    // Extract type from JSON Schema
                    string jsonType = "string"; // default
                    if (paramSchema.TryGetProperty("type", out JsonElement typeElement))
                        jsonType = typeElement.GetString() ?? "string";

                    bool isRequired = requiredFields.Contains(paramName);

                    // Create ToolParameter (existing format, no Type field)
                    toolParams.Add(new Records.ToolParameter(
                        Name: paramName,
                        Description: description,
                        Required: isRequired,
                        Scope: "request", // MCP tools cannot have parameters with "context" scope
                        Shared: false
                    ));

                    // Create MCPParameterInfo (internal, with type tracking)
                    paramInfos.Add(new MCPParameterInfo(
                        Name: paramName,
                        ClrType: MapJsonTypeToClrType(jsonType),
                        JsonType: jsonType
                    ));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error parsing InputSchema for tool: {McpToolName}", mcpTool.Name);
        }

        return (toolParams, paramInfos);
    }

    /// <summary>
    /// Maps JSON Schema type to .NET CLR type.
    /// Supports: string, integer, number (double), boolean.
    /// DateTime is handled as string (ISO 8601 format).
    /// </summary>
    private static Type MapJsonTypeToClrType(string jsonType)
    {
        return jsonType?.ToLowerInvariant() switch
        {
            "integer" => typeof(int),
            "number" => typeof(double),
            "boolean" => typeof(bool),
            _ => typeof(string) // Default to string
        };
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