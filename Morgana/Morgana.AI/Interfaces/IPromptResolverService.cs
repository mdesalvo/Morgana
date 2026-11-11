namespace Morgana.AI.Interfaces;

/// <summary>Resolves a prompt by id from the framework prompts of morgana.json or the domain prompts of agents.json.</summary>
// Actors and agents ask by id and never learn where a prompt is stored, so files, a database or an API can replace the embedded resources.
public interface IPromptResolverService
{
    /// <summary>The prompt declared under <paramref name="promptID"/>, a framework prompt id or an intent name.</summary>
    /// <exception cref="KeyNotFoundException">No layer declares the id, which is a configuration fault.</exception>
    /// <exception cref="InvalidOperationException">Both layers declare the id, which neither may hide from the other.</exception>
    Task<Records.Prompt> ResolveAsync(string promptID);
}