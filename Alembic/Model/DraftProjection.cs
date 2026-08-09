using Morgana.AI;

namespace Alembic.Model;

/// <summary>
/// Projects Draft elements onto the framework's own record types.
/// </summary>
/// <remarks>
/// One place, two callers with different purposes: the exporter serializes what comes out of here,
/// and the recap hands it to <c>IPromptComposerService</c>. That they share this projection is the
/// point — a recap composed from a slightly different <see cref="Records.Prompt"/> than the one
/// that gets written would be a recap of a domain nobody is going to run.
/// <para>
/// A Draft field that is still <c>null</c> ("not asked yet") projects to the empty string, because
/// the framework's records have no notion of unanswered. Validation is what distinguishes the two;
/// this projection deliberately does not, and must not be read as accepting them as equivalent.
/// </para>
/// </remarks>
public static class DraftProjection
{
    /// <summary>
    /// The AdditionalProperties key carrying an agent's toolkit, matched ordinally — the way
    /// <see cref="Records.Prompt.GetAdditionalProperty{T}"/> matches it.
    /// </summary>
    public const string ToolsPropertyName = "Tools";

    /// <summary>
    /// Rebuilds an intent definition from its Draft element.
    /// </summary>
    public static Records.IntentDefinition ToIntentDefinition(IntentDraft intent) =>
        new(intent.Name ?? string.Empty,
            intent.Description ?? string.Empty,
            intent.Label,
            intent.DefaultValue);

    /// <summary>
    /// Rebuilds an agent prompt from its Draft element, putting the toolkit back into
    /// AdditionalProperties alongside whatever else was carried through.
    /// </summary>
    /// <remarks>
    /// The toolkit is written as its own entry, first, and the unmodelled entries follow. This does
    /// not necessarily reproduce the grouping a file arrived with — AdditionalProperties is a list
    /// of dictionaries and the same content can be spread across it in several ways — which is
    /// precisely why the round-trip invariant is stated as equivalence and not byte identity.
    /// Morgana looks keys up across every entry, so the grouping is not information.
    /// </remarks>
    public static Records.Prompt ToPrompt(AgentDraft agent)
    {
        List<Dictionary<string, object>> additionalProperties = [];

        if (agent.Tools.Count > 0)
            additionalProperties.Add(new Dictionary<string, object>
            {
                [ToolsPropertyName] = agent.Tools.Select(ToToolDefinition).ToList()
            });

        additionalProperties.AddRange(agent.UnmodelledProperties);

        return new Records.Prompt(
            agent.ID ?? string.Empty,
            agent.Type,
            agent.SubType,
            agent.Target ?? string.Empty,
            agent.Instructions ?? string.Empty,
            agent.Formatting ?? string.Empty,
            agent.Personality,
            agent.Language,
            agent.Version,
            additionalProperties);
    }

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
    /// A parameter carrying a value the model itself authors declares no scope. The framework's
    /// record types <c>Scope</c> as a non-nullable string, so "no scope" travels as the empty
    /// string — which is what the importer read it back from.
    /// </remarks>
    public static Records.ToolParameter ToToolParameter(ToolParameterDraft parameter) =>
        new(parameter.Name ?? string.Empty,
            parameter.Description ?? string.Empty,
            parameter.Required,
            parameter.Scope ?? string.Empty,
            parameter.Shared);
}
