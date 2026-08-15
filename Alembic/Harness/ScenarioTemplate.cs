using System.Text;

namespace Alembic.Harness;

/// <summary>
/// What a template needs the agent to have before it is worth deriving at all.
/// </summary>
/// <remarks>
/// The mechanical half of applicability, and only that half. Whether a toolkit contains an
/// irreversible action, or whether the Formatting withholds anything, is a reading of the domain
/// and belongs to the model — which answers <c>not-applicable</c> and is believed. Whether an agent
/// has two tools is arithmetic, and asking a model to do arithmetic on a list it was just handed is
/// a way of paying for an answer that could be wrong.
/// </remarks>
public enum TemplateRequirement
{
    /// <summary>Derivable for any agent, including an MCP-only one that declares no native tools.</summary>
    Always,

    /// <summary>Needs at least one native tool to name in an assertion.</summary>
    Tools,

    /// <summary>Needs two, because the use-case is about choosing between them.</summary>
    TwoTools,

    /// <summary>Needs at least one context-scoped parameter.</summary>
    Context
}

/// <summary>
/// One behavioural use-case, as a scenario with the domain taken out of it.
/// </summary>
/// <remarks>
/// <para>
/// Alembic's harness component is a set of these, authored once, in this repository, and shipped
/// inside the assembly. They are not copies of anything: PromptHarness's own suite tests framework
/// policy, which is the half a client must never duplicate, so there is nothing there to derive a
/// domain scenario from. What a template carries instead is the <em>shape</em> of a behaviour worth
/// protecting in any domain — a boundary, a confirmation, an absent subject — with every word of the
/// domain replaced by a placeholder.
/// </para>
/// <para>
/// A template is therefore two things in one file. The <c>#@</c> header is addressed to the model:
/// what this use-case is, when it does not apply, and what a good derivation of it decides. The body
/// below it is a scenario the harness would load if the placeholders were real — key order,
/// thresholds and comments already settled, because those are the decisions a domain expert has no
/// way to make and no reason to.
/// </para>
/// <para>
/// The body parses as YAML, which is why every placeholder is written inside quotes: <c>{</c> opens
/// a flow mapping, so a bare <c>{{...}}</c> would make the template unreadable to the very check
/// that keeps the derivation honest.
/// </para>
/// </remarks>
/// <param name="Name">File name without extension. Also the second half of every id derived from it.</param>
/// <param name="UseCase">The behaviour this protects, in one line.</param>
/// <param name="Requires">What the agent must have for a derivation to be possible at all.</param>
/// <param name="Derive">What the model is told about turning this into one domain's scenario.</param>
/// <param name="Body">The scenario shape, placeholders intact, exactly as the model receives it.</param>
public sealed record ScenarioTemplate(
    string Name,
    string UseCase,
    TemplateRequirement Requires,
    string Derive,
    string Body)
{
    /// <summary>
    /// The directive keys a header may carry. Closed, so a colon inside prose stays prose.
    /// </summary>
    private static readonly string[] Directives = ["use-case", "requires", "derive"];

    /// <summary>
    /// Every harness key this template uses, and therefore every key its derivation may.
    /// </summary>
    /// <remarks>
    /// Read off the body rather than declared beside it. A template is a scenario the harness would
    /// load, so it already states its vocabulary by using it, and a second statement of the same
    /// thing is a second thing to keep in step.
    /// </remarks>
    public IReadOnlySet<string> Keys => field ??= ScenarioDerivation.KeysOf(Body);

    /// <summary>
    /// Reads one template file.
    /// </summary>
    /// <param name="name">The file name without extension.</param>
    /// <param name="text">The file's whole contents.</param>
    /// <returns>The parsed template.</returns>
    /// <exception cref="InvalidOperationException">
    /// The header is incomplete or names an unknown requirement. Thrown rather than defaulted: a
    /// template is Alembic's own asset, so a broken one is a build-time mistake that must surface
    /// on the first derivation rather than quietly produce a scenario derived from half a brief.
    /// </exception>
    public static ScenarioTemplate Parse(string name, string text)
    {
        Dictionary<string, StringBuilder> directives = [];
        StringBuilder? current = null;
        StringBuilder body = new StringBuilder();

        foreach (string line in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (!line.StartsWith("#@", StringComparison.Ordinal))
            {
                // Everything after the header is the scenario, blank separator line included: what
                // the model is shown must be what a scenario really looks like, spacing and all.
                if (body.Length > 0 || line.Trim().Length > 0)
                    body.AppendLine(line);

                continue;
            }

            string content = line[2..].Trim();
            string? key = Directives.FirstOrDefault(k => content.StartsWith(k + ":", StringComparison.Ordinal));

            if (key is not null)
            {
                current = directives[key] = new StringBuilder();

                // "derive: |" opens a block; anything else on the line is the value itself.
                string rest = content[(key.Length + 1)..].Trim();

                if (rest is not "|")
                    current.Append(rest);

                continue;
            }

            // A continuation line, and the only reason the key set is closed: prose is free to
            // contain a colon, and a directive is not decided by punctuation.
            current?.AppendLine().Append(content);
        }

        // Local rather than a private method: it closes over `directives` and `name`, both of which
        // only exist for the duration of this one parse, so lifting it out would mean threading both
        // through as parameters for no reader's benefit.
        string Directive(string key) =>
            directives.TryGetValue(key, out StringBuilder? value) && value.Length > 0
                ? value.ToString().Trim()
                : throw new InvalidOperationException($"Scenario template '{name}' declares no {key}.");

        string requires = Directive("requires");

        return new ScenarioTemplate(
            name,
            Directive("use-case"),
            Enum.TryParse(requires.Replace("-", ""), ignoreCase: true, out TemplateRequirement parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"Scenario template '{name}' requires '{requires}', which is not one of "
                    + string.Join(", ", Enum.GetNames<TemplateRequirement>())),
            Directive("derive"),
            // Trailing newline restored after Trim() so the body still ends the way a YAML document
            // is expected to — Trim() only exists to drop the leading/trailing blank lines the header
            // parsing loop can leave behind, not to change the shape of the scenario itself.
            body.ToString().Trim() + "\n");
    }
}
