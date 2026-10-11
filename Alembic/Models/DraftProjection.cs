using Morgana.AI;

namespace Alembic.Models;

/// <summary>
/// Projects Draft elements onto the framework's own record types.
/// </summary>
/// <remarks>The exporter and the recap share this projection; a field not asked yet becomes an empty string that validation still tells apart.</remarks>
public static class DraftProjection
{
    /// <summary>
    /// Rebuilds an intent definition from its Draft element.
    /// </summary>
    public static Records.IntentDefinition ToIntentDefinition(IntentDraft intent) =>
        // Fields not asked yet project to empty strings, which the framework's records can carry.
        new(intent.Name ?? string.Empty,
            intent.Description ?? string.Empty,
            intent.Label ?? string.Empty,
            intent.DefaultValue ?? string.Empty);

    /// <summary>
    /// Rebuilds an agent prompt from its Draft element, carrying the unmodelled AdditionalProperties through.
    /// </summary>
    /// <remarks>The prompt carries neither the toolkit nor the workflows, since the framework reads both off the tool and workflow classes.</remarks>
    public static Records.Prompt ToPrompt(AgentDraft agent)
    {
        // The unmodelled entries travel as they arrived: Morgana looks keys up across all of them, so the grouping carries no information.
        return new Records.Prompt(
            agent.ID ?? string.Empty,
            agent.Target ?? string.Empty,
            agent.Instructions ?? string.Empty,
            agent.Formatting ?? string.Empty,
            agent.Personality,
            agent.Territory,
            agent.Language,
            agent.Version)
        {
            // The keys Alembic does not model travel back out untouched.
            AdditionalProperties = [.. agent.UnmodelledProperties]
        };
    }

    /// <summary>
    /// Rebuilds the definition the framework would project from the tool's class, for the readers inside Alembic.
    /// </summary>
    public static Records.ToolDefinition ToToolDefinition(ToolDraft tool) =>
        // Fields not asked yet project to empty strings; the parameters project one by one.
        new(tool.Name ?? string.Empty,
            tool.Description ?? string.Empty,
            [.. tool.Parameters.Select(ToToolParameter)],
            RequiresExecutionApproval: tool.RequiresExecutionApproval,
            // A tool that declared no field leaves Returns out, the way a method returning no record projects none.
            Returns: tool.Returns.Count > 0
                ? [.. tool.Returns.Select(field => new Records.ToolReturn(
                    field.Name ?? string.Empty,
                    field.Description ?? string.Empty,
                    field.Name == Constants.Workflows.FailureField))]
                : null);

    /// <summary>
    /// Rebuilds a workflow definition from its Draft element; the carried properties are the distinct names across its edges.
    /// </summary>
    public static Records.WorkflowDefinition ToWorkflowDefinition(WorkflowDraft workflow) =>
        // Steps and edges go across as declared; the carried names are read off the edges.
        new(workflow.Name ?? string.Empty,
            workflow.Description ?? string.Empty,
            [.. workflow.Steps],
            [.. workflow.Edges],
            CarriedNames(workflow));

    /// <summary>
    /// The names that a workflow's edges carry, distinct and in order of first appearance.
    /// </summary>
    public static List<string> CarriedNames(WorkflowDraft workflow) =>
        // An edge that carries nothing contributes no name; a name carried twice counts once.
        [.. workflow.Edges.SelectMany(edge => edge.Carrying ?? []).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Rebuilds a tool parameter from its Draft element.
    /// </summary>
    /// <remarks>A scope not settled yet travels as the empty string, since the framework's record does not allow <c>null</c>.</remarks>
    public static Records.ToolParameter ToToolParameter(ToolParameterDraft parameter) =>
        // The parameter keeps its requiredness and its sharing exactly as the client settled them.
        new(parameter.Name ?? string.Empty,
            parameter.Description ?? string.Empty,
            parameter.Required,
            parameter.Scope ?? string.Empty,
            parameter.Shared);
}
