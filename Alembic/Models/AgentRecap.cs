namespace Alembic.Models;

/// <summary>
/// The prompt and the tools that one agent's model reads, composed from the Draft by the framework's own composer.
/// </summary>
/// <remarks>The framework's per-turn held-context declaration is absent, since it belongs to a session that does not exist while the domain is authored.</remarks>
/// <param name="AgentId">The agent the recap belongs to.</param>
/// <param name="SystemPrompt">The two-layer system prompt the model reads once before anything else.</param>
/// <param name="Tools">Each tool as the model weighs it mid-turn.</param>
public sealed record AgentRecap(
    string AgentId,
    string SystemPrompt,
    IReadOnlyList<ToolRecap> Tools);

/// <summary>
/// One tool as the model reads it, with the description the author wrote and the parameters it fills.
/// </summary>
/// <param name="Name">The tool name, which is also the C# method name.</param>
/// <param name="Description">The description as written, since the framework splices nothing onto it.</param>
/// <param name="Parameters">The parameters as their JSON schema carries them.</param>
public sealed record ToolRecap(
    string Name,
    string Description,
    IReadOnlyList<ParameterRecap> Parameters);

/// <summary>
/// One tool parameter as the model reads it in the JSON schema.
/// </summary>
/// <remarks>A parameter left undescribed is emitted bare, since the framework adds no template to its description.</remarks>
/// <param name="Name">The parameter name.</param>
/// <param name="Description">The description as the author wrote it.</param>
/// <param name="Required">Whether the model must supply the value.</param>
/// <param name="Scope">Where the value comes from; <c>null</c> where the parameter declares none.</param>
public sealed record ParameterRecap(
    string Name,
    string Description,
    bool Required,
    string? Scope);
