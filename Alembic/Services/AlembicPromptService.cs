using System.Reflection;
using System.Text;
using System.Text.Json;
using Alembic.Interfaces;
using Morgana.AI;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IAlembicPromptService"/>: loads <c>alembic.json</c> from this assembly's
/// embedded resources.
/// </summary>
/// <remarks>
/// A near-copy of how <c>ConfigurationPromptResolverService</c> loads <c>morgana.json</c>, down to
/// matching the resource by its file-name suffix rather than its namespace-prefixed manifest name,
/// so a rename of the assembly or root namespace does not silently lose the prose.
/// </remarks>
public class AlembicPromptService : IAlembicPromptService
{
    /// <summary>
    /// The prompts, parsed once on first use.
    /// </summary>
    private readonly Lazy<Records.Prompt[]> prompts = new(LoadPrompts);

    /// <inheritdoc />
    public Records.Prompt Resolve(string promptId) =>
        prompts.Value.FirstOrDefault(p => string.Equals(p.ID, promptId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Prompt '{promptId}' is not declared in alembic.json.");

    /// <inheritdoc />
    public string Compose(string promptId)
    {
        Records.Prompt prompt = Resolve(promptId);

        StringBuilder sb = new StringBuilder();

        sb.AppendLine(prompt.Target);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(prompt.Personality))
        {
            sb.AppendLine(prompt.Personality);
            sb.AppendLine();
        }

        sb.AppendLine(prompt.Instructions);
        sb.AppendLine();
        sb.AppendLine(prompt.Formatting);

        return sb.ToString();
    }

    /// <summary>
    /// Reads <c>alembic.json</c> out of this assembly.
    /// </summary>
    private static Records.Prompt[] LoadPrompts()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();

        string resourceName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith(".alembic.json", StringComparison.OrdinalIgnoreCase));

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("Resource alembic.json is not embedded in Alembic.");

        Records.PromptCollection? collection = JsonSerializer.Deserialize<Records.PromptCollection>(
            stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Unlike the framework's resolver, this one does not degrade to an empty set: Alembic
        // without its own prose is not a diminished Alembic, it is an interviewer with nothing to
        // say. Failing here, loudly, beats conducting an interview on an empty system prompt.
        return collection?.Prompts is { Length: > 0 } loaded
            ? loaded
            : throw new InvalidOperationException("alembic.json declares no prompts.");
    }
}
