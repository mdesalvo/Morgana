using System.Reflection;
using Alembic.Model;

namespace Alembic.Harness;

/// <summary>
/// Alembic's behavioural use-cases, read once out of its own assembly.
/// </summary>
/// <remarks>
/// The backpack, packed at compile time. Alembic lives wherever the client deployed it and there is
/// no repository beside it there, so a template that is not embedded is a template that does not
/// exist at runtime. Adding one is a file in <c>Harness/Templates/</c> and nothing else: the
/// <c>EmbeddedResource</c> glob picks it up and this reads whatever it finds.
/// </remarks>
public static class ScenarioTemplateLibrary
{
    /// <summary>
    /// The manifest segment the templates share, from their folder.
    /// </summary>
    private const string Folder = ".Harness.Templates.";

    /// <summary>
    /// Every template, parsed once, in file-name order.
    /// </summary>
    /// <remarks>
    /// Ordered so a domain derives its scenarios in the same sequence on every run: a second emit
    /// of an unchanged Draft should differ from the first in what the model wrote, never in what it
    /// was asked and when.
    /// </remarks>
    public static IReadOnlyList<ScenarioTemplate> All { get; } = Load();

    /// <summary>
    /// Every harness key any template uses: the whole vocabulary Alembic can vouch for.
    /// </summary>
    /// <remarks>
    /// What a scenario outside the templated base is held to. It is a union rather than a schema
    /// because Alembic has no schema — the templates are its only knowledge of what the harness
    /// binds, and a key none of them uses is a key nothing here has ever seen work. The harness's
    /// own loader would drop such a key without a sound, so the union is exactly the line between an
    /// assertion and the appearance of one.
    /// </remarks>
    public static IReadOnlySet<string> Vocabulary { get; } =
        All.SelectMany(template => template.Keys).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The templates worth spending a call on for this agent.
    /// </summary>
    /// <param name="agent">The agent about to be exercised.</param>
    /// <remarks>
    /// Only the arithmetic is decided here. Every template that survives this filter may still come
    /// back <c>not-applicable</c>, and that is the model reading the domain rather than counting it.
    /// </remarks>
    public static IEnumerable<ScenarioTemplate> For(AgentDraft agent) =>
        All.Where(template => template.Requires switch
        {
            TemplateRequirement.Tools => agent.Tools.Count >= 1,
            TemplateRequirement.TwoTools => agent.Tools.Count >= 2,
            TemplateRequirement.Context => agent.Tools.Any(tool =>
                tool.Parameters.Any(parameter => parameter.Scope == "context")),
            _ => true
        });

    /// <summary>
    /// Reads and parses the embedded templates.
    /// </summary>
    private static IReadOnlyList<ScenarioTemplate> Load()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();

        List<ScenarioTemplate> templates = [];

        foreach (string resource in assembly.GetManifestResourceNames()
                     .Where(n => n.Contains(Folder, StringComparison.Ordinal)
                                 && n.EndsWith(".yaml", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(resource)!;
            using StreamReader reader = new StreamReader(stream);

            int start = resource.IndexOf(Folder, StringComparison.Ordinal) + Folder.Length;
            string name = resource[start..^".yaml".Length];

            templates.Add(ScenarioTemplate.Parse(name, reader.ReadToEnd()));
        }

        // An Alembic with no use-cases would emit an empty Scenarios/ directory and call the archive
        // complete, which is the one failure mode a client cannot see. Louder than that, earlier.
        return templates.Count > 0
            ? templates
            : throw new FileNotFoundException(
                $"No scenario templates are embedded in Alembic under '{Folder.Trim('.')}'.");
    }
}
