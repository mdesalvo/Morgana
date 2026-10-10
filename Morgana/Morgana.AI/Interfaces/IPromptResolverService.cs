namespace Morgana.AI.Interfaces;

/// <summary>
/// Resolves a prompt by id from one registry that holds the framework's prompts (Morgana, Classifier, Guard, Presentation, ChannelAdapter)
/// and the domain's prompts (one per intent). Callers never learn where a prompt is stored.
/// </summary>
public interface IPromptResolverService
{
    /// <summary>
    /// Returns every available prompt, framework and domain alike, for startup diagnostics and validation.
    /// </summary>
    Task<Records.Prompt[]> GetAllPromptsAsync();

    /// <summary>
    /// Resolves a prompt by exact id: a framework prompt name or a domain intent name.
    /// Throws InvalidOperationException when the id is unknown, since a prompt that cannot be found is a configuration fault.
    /// </summary>
    Task<Records.Prompt> ResolveAsync(string promptID);
}