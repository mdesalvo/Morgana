namespace Alembic.Models;

/// <summary>
/// What one agent's model actually reads, composed from the Draft through the framework's own <c>IPromptComposerService</c>.
/// The prompt and the tools stay apart, read at different moments; the per-turn held-context declaration is absent, since no session exists at authoring time.
/// </summary>
/// <param name="AgentId">Which agent this is.</param>
/// <param name="SystemPrompt">The composed two-layer system prompt: framework layer, fences, global policies, then the domain layer.</param>
/// <param name="Tools">Each tool as the model weighs it, with the context guidance spliced in where the tool declares context-scoped parameters.</param>
public sealed record AgentRecap(
    string AgentId,
    string SystemPrompt,
    IReadOnlyList<ToolRecap> Tools);

/// <summary>One tool as the model reads it.</summary>
/// <param name="Name">Tool name, which is also the C# method name.</param>
/// <param name="Description">What the author wrote, plus ToolDescriptionContextGuidance where the tool declares context-scoped parameters.</param>
/// <param name="Parameters">The parameters, as their JSON schema will carry them.</param>
public sealed record ToolRecap(
    string Name,
    string Description,
    IReadOnlyList<ParameterRecap> Parameters);

/// <summary>
/// One parameter as its JSON schema will carry it, unmodified since the framework splices nothing onto a parameter description.
/// </summary>
/// <param name="Name">Parameter name.</param>
/// <param name="Description">Exactly what the author wrote.</param>
/// <param name="Required">Whether the model must supply it.</param>
/// <param name="Scope">Its scope; <c>null</c> where it declares none.</param>
public sealed record ParameterRecap(
    string Name,
    string Description,
    bool Required,
    string? Scope);