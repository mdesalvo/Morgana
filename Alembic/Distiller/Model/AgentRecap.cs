namespace Distiller.Model;

/// <summary>
/// What one agent's model actually reads, composed from the Draft through the framework's own
/// <c>IPromptComposerService</c>.
/// </summary>
/// <remarks>
/// Not a summary of the client's answers: the same service, the same fences, the same policy
/// ordering the running agent gets. The two parts below are shown apart rather than concatenated —
/// each is read by the model at a different moment, and seeing them merged would blur that.
/// <para>
/// What is deliberately absent is the framework's per-turn held-context declaration. It is a global
/// policy's own injection template, not anything Alembic or the domain author wrote, and it can only
/// ever be shown here against a supposed session that does not exist at authoring time — showing a
/// framework policy body in Alembic is exactly the layering violation the two-layer composition
/// exists to avoid everywhere else.
/// </para>
/// </remarks>
/// <param name="AgentId">Which agent this is.</param>
/// <param name="SystemPrompt">
/// The composed two-layer system prompt: framework layer, fences, global policies, then the domain
/// layer. Read once, before anything else.
/// </param>
/// <param name="Tools">
/// Each tool as the model weighs it, with the framework's context guidance already spliced in
/// where the tool declares context-scoped parameters.
/// </param>
public sealed record AgentRecap(
    string AgentId,
    string SystemPrompt,
    IReadOnlyList<ToolRecap> Tools);

/// <summary>
/// One tool as the model reads it.
/// </summary>
/// <param name="Name">Tool name, which is also the C# method name.</param>
/// <param name="Description">
/// The composed description: what the author wrote, plus <c>ToolDescriptionContextGuidance</c>
/// where the tool declares context-scoped parameters.
/// </param>
/// <param name="Parameters">The parameters, as their JSON schema will carry them.</param>
public sealed record ToolRecap(
    string Name,
    string Description,
    IReadOnlyList<ParameterRecap> Parameters);

/// <summary>
/// One parameter as its JSON schema will carry it.
/// </summary>
/// <remarks>
/// Shown unmodified on purpose, and that is the fact worth seeing: the framework splices no
/// template onto a parameter description. What the author wrote is the whole of what the model
/// reads at this rung, and a parameter left undescribed is emitted bare.
/// </remarks>
/// <param name="Name">Parameter name.</param>
/// <param name="Description">Exactly what the author wrote.</param>
/// <param name="Required">Whether the model must supply it.</param>
/// <param name="Scope">Its scope, or <c>null</c> where it declares none.</param>
public sealed record ParameterRecap(
    string Name,
    string Description,
    bool Required,
    string? Scope);
