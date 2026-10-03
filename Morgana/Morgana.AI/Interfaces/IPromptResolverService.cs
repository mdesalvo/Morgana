namespace Morgana.AI.Interfaces;

/// <summary>
/// Resolves prompts from configuration sources: framework prompts (morgana.json) and domain prompts (agents.json).
/// Abstracts prompt storage/retrieval to decouple actors/agents from specific storage mechanisms (embedded resources, files, databases, APIs).
/// Framework prompts: <see cref="Constants.Morgana"/> plus the ids of <see cref="Constants.Prompts"/>. Domain prompts: billing, contract, etc.
/// Default implementation: ConfigurationPromptResolverService merges morgana.json + agents.json into unified registry.
/// </summary>
public interface IPromptResolverService
{
    /// <summary>
    /// Resolves a prompt by ID from framework or domain sources: a framework id or a domain intent name (agents.json).
    /// The two vocabularies are disjoint, so an ID declared in both must fail rather than let one layer hide the other.
    /// Throws KeyNotFoundException if the ID is declared in neither (fail-fast).
    /// </summary>
    Task<Records.Prompt> ResolveAsync(string promptID);
}