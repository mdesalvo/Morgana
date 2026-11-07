using Morgana.AI;

namespace Alembic.Models;

/// <summary>
/// Projects Draft elements onto the framework's own record types.
/// </summary>
/// <remarks>
/// One place, two callers with different purposes: the exporter serializes what comes out of here
/// while the recap hands it to <c>IPromptComposerService</c>. That they share this projection is the
/// point — a recap composed from a slightly different <see cref="Records.Prompt"/> than the one
/// that gets written would be a recap of a domain nobody is going to run.
/// <para>
/// A Draft field that is still <c>null</c> ("not asked yet") projects to the empty string, because
/// the framework's records have no notion of unanswered. Validation is what distinguishes the two;
/// this projection deliberately does not and must not be read as accepting them as equivalent.
/// </para>
/// </remarks>
public static class DraftProjection
{
    /// <summary>
    /// Rebuilds an intent definition from its Draft element.
    /// </summary>
    public static Records.IntentDefinition ToIntentDefinition(IntentDraft intent) =>
        new(intent.Name ?? string.Empty,
            intent.Description ?? string.Empty,
            intent.Label ?? string.Empty,
            intent.DefaultValue ?? string.Empty);

    /// <summary>
    /// Rebuilds an agent prompt from its Draft element, carrying through the unmodelled AdditionalProperties.
    /// </summary>
    /// <remarks>
    /// The toolkit is not written: Morgana reads a tool from the C# declaration and AdditionalProperties holds only what Alembic carries verbatim.
    /// </remarks>
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

    /// <summary>
    /// Rebuilds a tool definition from its Draft element.
    /// </summary>
    public static Records.ToolDefinition ToToolDefinition(ToolDraft tool) =>
        new(tool.Name ?? string.Empty,
            tool.Description ?? string.Empty,
            [.. tool.Parameters.Select(ToToolParameter)]);

    /// <summary>
    /// Rebuilds a tool parameter from its Draft element.
    /// </summary>
    /// <remarks>
    /// A value the model itself authors has no scope.
    /// </remarks>
    public static Records.ToolParameter ToToolParameter(ToolParameterDraft parameter) =>
        new(parameter.Name ?? string.Empty,
            parameter.Description ?? string.Empty,
            parameter.Required,
            parameter.Scope,
            parameter.Shared);
}
