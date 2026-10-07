using Morgana.AI;

namespace Alembic.Model;

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
            intent.Label,
            intent.DefaultValue);

    /// <summary>
    /// Rebuilds an agent prompt from its Draft element, carrying the unmodelled
    /// AdditionalProperties through.
    /// </summary>
    /// <remarks>
    /// The toolkit is never written: the framework refuses a <c>Tools</c> key in <c>agents.json</c>, since the tool class declares it.
    /// The workflows are classes, so the prompt carries none. The unmodelled entries are written as they arrived. This does
    /// not necessarily reproduce the grouping a file arrived with — AdditionalProperties is a list
    /// of dictionaries and the same content can be spread across it in several ways — which is
    /// precisely why the round-trip invariant is stated as equivalence and not byte identity.
    /// Morgana looks keys up across every entry, so the grouping is not information.
    /// </remarks>
    public static Records.Prompt ToPrompt(AgentDraft agent)
    {
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
            AdditionalProperties = [.. agent.UnmodelledProperties]
        };
    }

    /// <summary>
    /// Rebuilds the definition the framework would project from the tool's class, for the readers inside Alembic.
    /// </summary>
    public static Records.ToolDefinition ToToolDefinition(ToolDraft tool) =>
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
        new(workflow.Name ?? string.Empty,
            workflow.Description ?? string.Empty,
            [.. workflow.Steps],
            [.. workflow.Edges],
            CarriedNames(workflow));

    /// <summary>
    /// The names that a workflow's edges carry, distinct and in order of first appearance.
    /// </summary>
    public static List<string> CarriedNames(WorkflowDraft workflow) =>
        [.. workflow.Edges.SelectMany(edge => edge.Carrying ?? []).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Rebuilds a tool parameter from its Draft element.
    /// </summary>
    /// <remarks>
    /// The framework's record types <c>Scope</c> as a non-nullable string, so a scope not settled yet
    /// travels as the empty string.
    /// </remarks>
    public static Records.ToolParameter ToToolParameter(ToolParameterDraft parameter) =>
        new(parameter.Name ?? string.Empty,
            parameter.Description ?? string.Empty,
            parameter.Required,
            parameter.Scope ?? string.Empty,
            parameter.Shared);
}
