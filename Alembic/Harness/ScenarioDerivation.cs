using System.Text;
using YamlDotNet.RepresentationModel;

namespace Alembic.Harness;

/// <summary>
/// What came back from deriving one template against one domain, and whether it can be trusted.
/// </summary>
/// <param name="Content">The scenario as it will be written, id imposed. Null when nothing usable came back.</param>
/// <param name="NotApplicable">Why the model declined to derive this use-case, when it did.</param>
/// <param name="Problem">What is wrong with a derivation that is being shipped anyway.</param>
public sealed record DerivedScenario(string? Content, string? NotApplicable, string? Problem);

/// <summary>
/// Checks a derivation against the template it came from.
/// </summary>
/// <remarks>
/// <para>
/// The check is a subset rule and nothing more: <b>a derivation may drop a key, and may never add
/// one</b>. That is enough because the template is the vocabulary — every key in it is one Alembic
/// authored knowing the harness binds it, so a key that was not there is a key nobody verified.
/// </para>
/// <para>
/// It has to be checked here because it cannot be caught later. <c>ScenarioLoader</c>'s deserializer
/// is built <c>.IgnoreUnmatchedProperties()</c> — right where it is, since a suite that refused to
/// load over one stray key would be brittle in the hands of the person editing it — which means an
/// unrecognised key is <b>dropped without a sound</b>: the scenario loads, runs, passes and asserts
/// nothing. It reads as coverage and is not, and a model reaching for a plausible key that does not
/// exist (<c>textContains</c>, say) produces exactly that.
/// </para>
/// <para>
/// A derivation that fails still ships, with the problem written across its top. Alembic has no
/// business deciding a client may not see its own artifact, and a silently missing scenario costs
/// them more than a visibly broken one.
/// </para>
/// </remarks>
public static class ScenarioDerivation
{
    /// <summary>
    /// How the model declines a use-case its reading of the domain does not support.
    /// </summary>
    public const string NotApplicableMarker = "not-applicable:";

    /// <summary>
    /// Verifies one answer and prepares it for writing.
    /// </summary>
    /// <param name="allowed">
    /// Every key this answer may use: one template's, or the whole library's for a scenario the
    /// domain asked for that no template anticipated.
    /// </param>
    /// <param name="id">The id Alembic imposes, which is also the file name.</param>
    /// <param name="answer">What the model returned, fence already stripped.</param>
    public static DerivedScenario Check(IReadOnlySet<string> allowed, string id, string answer)
    {
        string text = answer.Trim();

        if (text.Length == 0)
            return new DerivedScenario(null, null, "the model returned nothing");

        // Declining is a first-class answer, and cheaper than a scenario nobody should have asked
        // for: a read-only toolkit has no confirmation to protect, and pretending otherwise would
        // hand the client a test that fails a correct agent.
        if (text.StartsWith(NotApplicableMarker, StringComparison.OrdinalIgnoreCase))
            return new DerivedScenario(null, text[NotApplicableMarker.Length..].Trim(), null);

        // Everything below reports rather than rejects, so the checks run in the order a reader
        // would want them: what makes the file useless first, what makes it weaker after.
        string? problem =
            Unresolved(text)
            ?? Structure(allowed, text, out YamlMappingNode? root)
            ?? Substance(root);

        return new DerivedScenario(Identify(text, id), null, problem);
    }

    /// <summary>
    /// Catches a placeholder the derivation left behind.
    /// </summary>
    /// <remarks>
    /// A scenario still carrying <c>{{the tool that produces the answer}}</c> is a template with a
    /// domain glued to one half of it. It parses, and it asserts against a tool no agent has.
    /// </remarks>
    private static string? Unresolved(string text) =>
        text.Contains("{{", StringComparison.Ordinal)
            ? "it still carries a placeholder from the template, so at least one assertion names something that does not exist"
            : null;

    /// <summary>
    /// Parses the derivation and holds it to the vocabulary it was allowed.
    /// </summary>
    private static string? Structure(IReadOnlySet<string> allowed, string text, out YamlMappingNode? root)
    {
        root = null;

        try
        {
            root = Read(text);
        }
        catch (Exception ex)
        {
            // YamlDotNet names the line and the character, which is the whole reason for parsing
            // rather than pattern-matching: the client is told what to fix, not that something is wrong.
            return "it is not valid YAML — " + ex.Message.ReplaceLineEndings(" ").Trim();
        }

        if (root is null)
            return "it is not a scenario document";

        List<string> invented = [.. Keys(root).Except(allowed, StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        // Stated as what Alembic knows rather than as what the harness does, because after the
        // templates became the only vocabulary it no longer knows the second thing. Both readings
        // are bad and the client can tell them apart at a glance: a key the harness does not bind
        // asserts nothing at all, and one it does bind here asserts framework behaviour that
        // Morgana's own suite already covers and that this suite has no business restating.
        return invented.Count == 0
            ? null
            : $"it uses {string.Join(", ", invented.Select(k => $"'{k}'"))}, which is outside the domain "
              + "vocabulary — either the harness does not bind it, and the scenario runs, passes and "
              + "asserts nothing there, or it does and this is re-testing Morgana rather than your domain";
    }

    /// <summary>
    /// Catches a scenario that would run and prove nothing.
    /// </summary>
    private static string? Substance(YamlMappingNode? root)
    {
        if (root is null)
            return null;

        return root.Children.TryGetValue(new YamlScalarNode("turns"), out YamlNode? turns)
               && turns is YamlSequenceNode { Children.Count: > 0 }
            ? null
            : "it has no turns, so it would load and assert nothing";
    }

    /// <summary>
    /// Rewrites the <c>id:</c> line to the one Alembic chose.
    /// </summary>
    /// <remarks>
    /// The id is the file name and the string a test names, so it is Alembic's by right: derived
    /// from the intent and the template, it is unique across a domain by construction and needs no
    /// collision rule. Rewritten in place rather than re-serialized, because re-serializing would
    /// discard the comments — and a comment saying why a turn admits two shapes is how the next
    /// person knows the looseness was deliberate.
    /// </remarks>
    private static string Identify(string text, string id)
    {
        StringBuilder sb = new StringBuilder();
        bool written = false;

        foreach (string line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (!written && line.StartsWith("id:", StringComparison.Ordinal))
            {
                sb.AppendLine($"id: {id}");
                written = true;

                continue;
            }

            sb.AppendLine(line);
        }

        return written ? sb.ToString() : $"id: {id}\n" + sb;
    }

    /// <summary>
    /// Reads a document's root mapping, or null when there is none.
    /// </summary>
    private static YamlMappingNode? Read(string yaml)
    {
        YamlStream stream = new YamlStream();
        stream.Load(new StringReader(yaml));

        return stream.Documents.Count > 0 ? stream.Documents[0].RootNode as YamlMappingNode : null;
    }

    /// <summary>
    /// Every key a document uses, which is how a template states its own vocabulary.
    /// </summary>
    /// <remarks>
    /// The templates are the only description of the harness's schema that Alembic holds, and this
    /// is what reads it back out of them. Nothing is declared twice: a key exists for a derivation
    /// because a template Alembic ships put it there, having been written knowing the harness binds
    /// it.
    /// </remarks>
    public static IReadOnlySet<string> KeysOf(string yaml) => Keys(Read(yaml));

    /// <summary>
    /// Every mapping key anywhere in a document.
    /// </summary>
    /// <remarks>
    /// Flat rather than by path, deliberately. A key is bound by name to a property of one of three
    /// types, and the interesting mistake is inventing a name — not putting a real key at the wrong
    /// depth, which a flat set still catches on the way in from the other direction.
    /// </remarks>
    private static HashSet<string> Keys(YamlNode? node)
    {
        HashSet<string> keys = new(StringComparer.Ordinal);

        void Walk(YamlNode? current)
        {
            switch (current)
            {
                case YamlMappingNode mapping:
                    foreach ((YamlNode key, YamlNode value) in mapping.Children)
                    {
                        if (key is YamlScalarNode { Value: { } name })
                            keys.Add(name);

                        Walk(value);
                    }

                    break;

                case YamlSequenceNode sequence:
                    foreach (YamlNode child in sequence.Children)
                        Walk(child);

                    break;
            }
        }

        Walk(node);

        return keys;
    }
}
