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
    /// Resolves a prompt by exact id: a framework prompt name or a domain intent name.
    /// Throws KeyNotFoundException when the id is unknown, since a prompt that cannot be found is a configuration fault.
    /// Throws InvalidOperationException when the id is declared both by the framework and by the domain, because the two vocabularies are disjoint and neither layer may hide the other.
    /// </summary>
    Task<Records.Prompt> ResolveAsync(string promptID);
}