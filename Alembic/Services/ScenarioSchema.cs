using System.Reflection;
using PromptHarness.Infrastructure.Engine;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Alembic.Services;

/// <summary>
/// PromptHarness's scenario schema, read off the harness's own types.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ScenarioDefinition"/> is compiled into Alembic as a linked source file rather than
/// described in prose, and this class is why. The harness's loader is built
/// <c>.IgnoreUnmatchedProperties()</c>, so a key it does not recognise is <b>dropped in silence</b>:
/// the scenario loads, runs, passes, and asserts nothing. A hand-maintained list of keys would drift
/// from the real one exactly once and produce that failure without a sound.
/// </para>
/// <para>
/// So both ends come from the same place. <see cref="ExpectKeys"/> is reflected off
/// <c>ExpectSpec</c> and rendered into the briefing the model reads; <see cref="Verify"/> parses
/// what came back with a <b>strict</b> deserializer — the harness's own configuration minus the
/// forgiveness — so an invented key is a named error here instead of a silent nothing later. A
/// rename in the harness now breaks Alembic's build rather than Alembic's output.
/// </para>
/// </remarks>
public static class ScenarioSchema
{
    /// <summary>
    /// Naming convention shared with the harness: YAML keys are camelCase.
    /// </summary>
    private static readonly INamingConvention Naming = CamelCaseNamingConvention.Instance;

    /// <summary>
    /// Strict deserializer: the harness's, without <c>IgnoreUnmatchedProperties</c>.
    /// </summary>
    /// <remarks>
    /// The forgiveness is right where it is — a harness that refused to load a scenario over one
    /// stray key would be brittle in the hands of the person editing it. It is wrong here, because
    /// Alembic is checking a machine's output before a human has ever read it.
    /// </remarks>
    private static readonly IDeserializer Strict = new DeserializerBuilder()
        .WithNamingConvention(Naming)
        .Build();

    /// <summary>
    /// Every key an <c>expect:</c> block may carry, in YAML spelling.
    /// </summary>
    public static IReadOnlyList<string> ExpectKeys { get; } =
        [.. typeof(ExpectSpec)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => Naming.Apply(p.Name))
                .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every key a <c>turns:</c> entry may carry.
    /// </summary>
    public static IReadOnlyList<string> TurnKeys { get; } =
        [.. typeof(TurnDefinition)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => Naming.Apply(p.Name))
                .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every key a scenario document may carry at the top level.
    /// </summary>
    public static IReadOnlyList<string> ScenarioKeys { get; } =
        [.. typeof(ScenarioDefinition)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => Naming.Apply(p.Name))
                .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Renders the vocabulary as the table appended to the briefing.
    /// </summary>
    /// <remarks>
    /// Generated rather than written, so the list the model is given and the list it is judged
    /// against are the same list, and both are the harness's.
    /// </remarks>
    public static string Vocabulary() =>
        $"""
         A scenario document carries: {string.Join(", ", ScenarioKeys.Select(k => $"`{k}`"))}.

         A turn carries: {string.Join(", ", TurnKeys.Select(k => $"`{k}`"))}.

         An `expect` block carries these and NO others — the harness silently ignores a key it does
         not recognise, so an invented one produces a scenario that runs, passes and asserts nothing:

         {string.Join(", ", ExpectKeys.Select(k => $"`{k}`"))}.
         """;

    /// <summary>
    /// Parses one generated document the way the harness would, but strictly.
    /// </summary>
    /// <param name="yaml">One YAML document.</param>
    /// <returns>
    /// The scenario's id and what is wrong with it. A null id means it could not be parsed or
    /// declares none, which is fatal: <c>ScenarioLoader</c> keys everything off the id and throws on
    /// a file without one.
    /// </returns>
    public static (string? Id, string? Problem) Verify(string yaml)
    {
        try
        {
            ScenarioDefinition scenario = Strict.Deserialize<ScenarioDefinition>(yaml);

            if (string.IsNullOrWhiteSpace(scenario.Id))
                return (null, "the document declares no id, and ScenarioLoader throws on a file without one");

            if (scenario.Turns.Count == 0)
                return (scenario.Id, "the scenario has no turns, so it would run and assert nothing");

            return (scenario.Id, null);
        }
        catch (Exception ex)
        {
            // The strict deserializer's message already names the offending key and its line, which
            // is the whole reason for parsing rather than pattern-matching: the client is told what
            // to fix, not that something somewhere is wrong.
            return (IdOf(yaml), ex.Message.ReplaceLineEndings(" ").Trim());
        }
    }

    /// <summary>
    /// Reads the <c>id:</c> line out of a document that did not parse.
    /// </summary>
    /// <remarks>
    /// Textual on purpose, and only used on the failure path: a document strict parsing rejected
    /// still has to be written somewhere the client will find it, and its own id is the only name
    /// anybody would look for it under.
    /// </remarks>
    private static string? IdOf(string yaml) =>
        yaml.Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("id:", StringComparison.Ordinal))
            ?[3..]
            .Trim()
            .Trim('"', '\'');
}
