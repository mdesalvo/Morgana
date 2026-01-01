using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Actor;

namespace Morgana.AI;

public static class Records
{
    public record AgentRequest(
        string ConversationId,
        string? Content,
        ClassificationResult? Classification);

    public record AgentResponse(
        string Response,
        bool IsCompleted = true);

    public record BroadcastContextUpdate(
        string SourceAgentIntent,
        Dictionary<string, object> UpdatedValues);

    public record ReceiveContextUpdate(
        string SourceAgentIntent,
        Dictionary<string, object> UpdatedValues);
        
    public record ClassificationResponse(
        [property: JsonPropertyName("intent")] string Intent,
        [property: JsonPropertyName("confidence")] double Confidence);

    public record ClassificationResult(
        string Intent,
        Dictionary<string, string> Metadata);

    public record ActiveAgentResponse(
        string Response,
        bool IsCompleted,
        IActorRef AgentRef);

    public record PromptCollection(
        Prompt[] Prompts);

    public record Prompt(
        string ID,
        string Type,
        string SubType,
        string Content,
        string Instructions,
        string? Personality,
        string Language,
        string Version,
        List<Dictionary<string, object>> AdditionalProperties)
    {
        public T GetAdditionalProperty<T>(string additionalPropertyName)
        {
            foreach (Dictionary<string, object> additionalProperties in AdditionalProperties)
            {
                if (additionalProperties.TryGetValue(additionalPropertyName, out object value))
                {
                    JsonElement element = (JsonElement)value;
                    return element.Deserialize<T>();
                }
            }
            throw new KeyNotFoundException($"AdditionalProperty with key '{additionalPropertyName}' was not found in the prompt with id='{ID}'");
        }
    }

    public record GlobalPolicy(
        string Name,
        string Description,
        string Type,
        int Priority);

    public record ErrorAnswer(
        string Name,
        string Content);

    public record ToolDefinition(
        string Name,
        string Description,
        IReadOnlyList<ToolParameter> Parameters);

    public record ToolParameter(
        string Name,
        string Description,
        bool Required,
        string Scope,
        bool Shared = false);

    /// <summary>
    /// Result of tool registry validation.
    /// </summary>
    public record ToolRegistryValidationResult(
        bool IsValid,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> Errors);
    
    public record IntentDefinition(
        [property: JsonPropertyName("Name")] string Name,
        [property: JsonPropertyName("Description")] string Description,
        [property: JsonPropertyName("Label")] string? Label,
        [property: JsonPropertyName("DefaultValue")] string? DefaultValue = null);

    public record IntentCollection
    {
        public List<IntentDefinition> Intents { get; set; }

        public IntentCollection(List<IntentDefinition> intents)
        {
            Intents = intents;
        }

        // Get all intents as Dictionary<name, description> for classification
        public Dictionary<string, string> AsDictionary()
        {
            return Intents.ToDictionary(i => i.Name, i => i.Description);
        }

        // Get displayable intents (exclude "other" and those without labels)
        public List<IntentDefinition> GetDisplayableIntents()
        {
            return Intents
                .Where(i => !string.Equals(i.Name, "other", StringComparison.OrdinalIgnoreCase) 
                              && !string.IsNullOrEmpty(i.Label))
                .ToList();
        }
    }

    // Presentation flow records

    public record PresentationResponse(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("quickReplies")] List<QuickReplyDefinition> QuickReplies);

    public record QuickReplyDefinition(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("value")] string Value);

    // Context wrapper for Become/PipeTo pattern (ClassifierActor)
        
    public record ClassificationContext(
        ClassificationResult Result,
        IActorRef OriginalSender);
    
    // MCP
    
    /// <summary>
    /// MCP tool definition as returned by MCP server.
    /// Follows Model Context Protocol specification.
    /// </summary>
    public record MCPToolDefinition(
        string Name,
        string Description,
        MCPInputSchema InputSchema);

    /// <summary>
    /// MCP tool input schema (simplified JSON Schema subset).
    /// Defines the structure and validation rules for tool parameters.
    /// </summary>
    public record MCPInputSchema(
        string Type,  // "object"
        Dictionary<string, MCPParameterSchema> Properties,
        List<string> Required);

    /// <summary>
    /// Individual parameter schema for MCP tool.
    /// Supports type validation, enums, and default values.
    /// </summary>
    public record MCPParameterSchema(
        string Type,         // "string", "number", "boolean", "array"
        string Description,
        string? Format = null,      // "date-time", "email", etc.
        object? Default = null,
        List<string>? Enum = null); // Allowed values

    /// <summary>
    /// Result of MCP tool invocation.
    /// Contains either successful content or error information.
    /// </summary>
    public record MCPToolResult(
        bool IsError,
        string? Content,
        string? ErrorMessage,
        Dictionary<string, object>? Metadata = null);

    /// <summary>
    /// MCP server configuration from appsettings.json.
    /// Defines how to connect to and initialize MCP servers.
    /// </summary>
    public record MCPServerConfig(
        string Name,
        MCPServerType Type,
        bool Enabled,
        Dictionary<string, string>? AdditionalSettings = null);

    /// <summary>
    /// Types of MCP servers supported by Morgana.
    /// </summary>
    public enum MCPServerType
    {
        /// <summary>
        /// Remote HTTP MCP server (future implementation)
        /// </summary>
        HTTP,
        
        /// <summary>
        /// Custom in-process implementation
        /// </summary>
        InProcess
    }

    /// <summary>
    /// MCP server discovery response.
    /// Provides metadata about server capabilities.
    /// </summary>
    public record MCPServerInfo(
        string Name,
        string Version,
        List<string> Capabilities);
    
    /// <summary>
    /// Request model for tool invocation.
    /// </summary>
    public record InvokeToolRequest(
        string ServerName,
        Dictionary<string, object>? Parameters);
}