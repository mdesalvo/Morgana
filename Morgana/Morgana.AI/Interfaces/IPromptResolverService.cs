namespace Morgana.AI.Interfaces;

/// <summary>
/// Resolves a prompt by id from one registry that holds the framework's prompts (Morgana, Classifier, Guard, Presentation, ChannelAdapter)
/// and the domain's prompts (one per intent). Callers never learn where a prompt is stored.
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