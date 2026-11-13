using Morgana.AI;

namespace Alembic.Models;

/// <summary>
/// Projects Draft elements onto the framework's own record types, for the exporter and the recap alike.
/// Sharing it keeps the recap a recap of the domain that gets written; a field not asked yet projects to empty, which validation alone tells apart.
/// </summary>
public static class DraftProjection
{
    /// <summary>Rebuilds an intent definition from its Draft element.</summary>
    public static Records.IntentDefinition ToIntentDefinition(IntentDraft intent) =>
        new(intent.Name ?? string.Empty,
            intent.Description ?? string.Empty,
            intent.Label ?? string.Empty,
            intent.DefaultValue ?? string.Empty);

    /// <summary>
    /// Rebuilds an agent prompt from its Draft element, carrying through the unmodelled AdditionalProperties.
    /// The toolkit is not written, since Morgana reads a tool from its C# declaration.
    /// </summary>
    public static Records.Prompt ToPrompt(AgentDraft agent) =>
        new(agent.ID ?? string.Empty,
            agent.Target ?? string.Empty,
            agent.Instructions ?? string.Empty,
            agent.Formatting ?? string.Empty,
            agent.Personality,
            agent.Territory,
            agent.Language,
            agent.Version,
            agent.UnmodelledProperties);

    /// <summary>Rebuilds a tool definition from its Draft element.</summary>
    public static Records.ToolDefinition ToToolDefinition(ToolDraft tool) =>
        new(tool.Name ?? string.Empty,
            tool.Description ?? string.Empty,
            [.. tool.Parameters.Select(ToToolParameter)]);

    /// <summary>Rebuilds a tool parameter from its Draft element, a value the model itself authors keeping no scope.</summary>
    public static Records.ToolParameter ToToolParameter(ToolParameterDraft parameter) =>
        new(parameter.Name ?? string.Empty,
            parameter.Description ?? string.Empty,
            parameter.Required,
            parameter.Scope,
            parameter.Shared);
}
