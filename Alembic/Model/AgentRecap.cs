namespace Alembic.Model;

/// <summary>
/// What one agent's model actually reads, composed from the Draft through the framework's own
/// <c>IPromptComposerService</c>.
/// </summary>
/// <remarks>
/// Not a summary of the client's answers: the same service, the same fences, the same policy
/// ordering the running agent gets. The three parts below are the framework's placement ladder,
/// which is why they are shown apart rather than concatenated — each is read by the model at a
/// different moment, and seeing them merged is exactly the confusion the ladder exists to prevent.
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
/// <param name="HypotheticalHeldContext">
/// The per-turn held-context declaration, rendered against <paramref name="HypotheticalHeldVariables"/>.
/// <b>Hypothetical by construction</b>: this injection states which variables the session holds
/// <em>right now</em>, and no such session exists at authoring time. The template and the splice
/// are real; only the variable set is a supposition — the one where every context-scoped parameter
/// in the agent's toolkit happens to be populated.
/// </param>
/// <param name="HypotheticalHeldVariables">The supposed variable set, in the order the provider would render it.</param>
public sealed record AgentRecap(
    string AgentId,
    string SystemPrompt,
    IReadOnlyList<ToolRecap> Tools,
    string? HypotheticalHeldContext,
    IReadOnlyList<string> HypotheticalHeldVariables);

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
