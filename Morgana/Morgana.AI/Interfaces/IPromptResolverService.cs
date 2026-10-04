namespace Morgana.AI.Interfaces;

/// <summary>
/// Resolves prompts from configuration sources: framework prompts (morgana.json) and domain prompts (agents.json).
/// Abstracts prompt storage/retrieval to decouple actors/agents from specific storage mechanisms (embedded resources, files, databases, APIs).
/// Framework prompts: Morgana, Classifier, Guard, Presentation. Domain prompts: billing, contract, etc.
/// Default implementation: ConfigurationPromptResolverService merges morgana.json + agents.json into unified registry.
/// </summary>
public interface IPromptResolverService
{
    /// <summary>
    /// Returns all available prompts (framework + domain) from merged morgana.json and agents.json.
    /// Domain prompts override framework if IDs collide. Used for startup diagnostics, validation, admin UI.
    /// </summary>
    Task<Records.Prompt[]> GetAllPromptsAsync();

    /// <summary>
    /// Resolves a specific prompt by exact ID from framework or domain sources. Framework prompts: "Morgana", "Classifier",
    /// "Guard", "Presentation". Domain prompts: intent names (agents.json). Domain overrides framework on ID collision.
    /// Throws InvalidOperationException if ID not found (fail-fast). Resolved prompts include content, instructions, personality, tools.
    /// </summary>
    Task<Records.Prompt> ResolveAsync(string promptID);
}