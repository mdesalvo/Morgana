using System.Reflection;
using System.Text;
using System.Text.Json;
using Distiller.Interfaces;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Distiller.Services;

/// <summary>
/// Default <see cref="IAlembicPromptService"/>: loads <c>alembic.json</c> from this assembly's
/// embedded resources and composes it beneath Morgana's own voice.
/// </summary>
/// <remarks>
/// The loading is a near-copy of how <c>ConfigurationPromptResolverService</c> loads
/// <c>morgana.json</c>, down to matching the resource by its file-name suffix rather than its
/// namespace-prefixed manifest name, so renaming the assembly does not silently lose the prose.
/// </remarks>
public class AlembicPromptService : IAlembicPromptService
{
    /// <summary>
    /// The framework prompt Alembic inherits its voice from.
    /// </summary>
    private const string MorganaPromptId = "Morgana";

    /// <summary>
    /// The prompt holding what Alembic says in every interview, whichever interviewer is conducting
    /// it: its identity, how a Morgana domain runs, how it asks and how it answers.
    /// </summary>
    private const string AlembicPromptId = "Alembic";

    /// <summary>
    /// The two jobs a step can be doing, one of which is folded in beneath the shared prose.
    /// </summary>
    /// <remarks>
    /// Writing a section that does not exist and correcting one that does are different work and a
    /// pass reads only the one it is doing. Kept as two rows of the same file rather than as two
    /// files: everything else — the identity, the conducting rules, the voice, every tool
    /// declaration — is the same in both and a second copy of it is the duplication this prompt was
    /// already once rebuilt to remove.
    /// </remarks>
    private const string ComposingPromptId = "Composing";

    /// <inheritdoc cref="ComposingPromptId" />
    private const string CorrectingPromptId = "Correcting";

    // Fences, in the framework's own idiom and for the framework's own reason: two layers carry
    // overlapping section labels and without a boundary the composed prompt shows [PERSONALITY]
    // twice with nothing saying which is which.
    //
    // Two layers, not three and that is still true of what the model reads: Morgana, then Alembic.
    // What changed is where Alembic's half is stored. Four passes that differ only in which tools
    // they hold were carrying four copies of the same conducting rules, the same voice and the same
    // output format — 22 000 characters of which half was duplication and duplication in a prompt
    // is not merely long: it is four places to edit a rule and three chances to leave one behind.
    //
    // So the identical part lives once, in the "Alembic" prompt and a pass carries only what is
    // its own — what it settles, what it must leave alone, how it goes about that. The two are
    // merged section by section here, under one set of labels, because the composed prompt must
    // still be the four sections an agent prompt always is: the model sees no seam.
    private const string MorganaLayerHeader =
        "======== FENCE: MORGANA — WHOSE VESSEL YOU ARE ========\n" +
        "You are an instrument of Morgana. This is her voice and it is yours: it is not a description of someone else and it is not overridable.";
    private const string MorganaLayerFooter = "======== END OF FENCE ========";
    private const string AlembicLayerHeader =
        "======== ALEMBIC ========\n" +
        "What follows specialises Morgana's voice for the step of the interview you are conducting right now. It adds that and NOTHING ELSE. It never contradicts the layer above.";

    /// <summary>
    /// Alembic's own prompts, parsed once on first use.
    /// </summary>
    private readonly Lazy<Records.Prompt[]> alembicPrompts = new(LoadAlembicPrompts);

    /// <summary>
    /// Morgana's framework prompt, resolved once from <c>morgana.json</c> in Morgana.AI.
    /// </summary>
    private readonly Lazy<Task<Records.Prompt>> morganaPrompt;

    /// <summary>
    /// Initializes the prompt service.
    /// </summary>
    /// <param name="promptResolverService">Resolves Morgana's framework prompt.</param>
    public AlembicPromptService(IPromptResolverService promptResolverService)
    {
        morganaPrompt = new Lazy<Task<Records.Prompt>>(() => promptResolverService.ResolveAsync(MorganaPromptId));
    }

    /// <inheritdoc />
    /// <param name="promptId">A prompt's <c>ID</c> in <c>alembic.json</c> — one of the six pass ids
    /// (<c>DomainMapper</c>, <c>AgentTarget</c>, …) or a standalone one like <c>DomainValidator</c>
    /// or <c>CodeMocker</c>. Matched case-insensitively.</param>
    /// <returns>The prompt's four sections, as authored — unmerged with Morgana's or Alembic's shared layer.</returns>
    public Records.Prompt Resolve(string promptId) =>
        alembicPrompts.Value.FirstOrDefault(p => string.Equals(p.ID, promptId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Prompt '{promptId}' is not declared in alembic.json.");

    /// <inheritdoc />
    /// <param name="interviewerId">The pass whose own half of the second layer to fold in — e.g.
    /// <c>AgentTarget</c> or <c>AgentToolkit</c>. Resolved through <see cref="Resolve"/>, so an
    /// unknown id fails the same way here as it would calling that method directly.</param>
    /// <returns>The whole composed system prompt this pass's model will read: Morgana's layer, then
    /// Alembic's shared prose and this pass's own, merged section by section under one set of labels.</returns>
    public async Task<string> ComposeAsync(string interviewerId, bool correcting = false)
    {
        StringBuilder sb = new StringBuilder();

        // Morgana
        Records.Prompt morgana = await morganaPrompt.Value;
        sb.AppendLine(MorganaLayerHeader);
        sb.AppendLine();
        sb.AppendLine(morgana.Target);
        sb.AppendLine();
        sb.AppendLine(morgana.Personality);
        sb.AppendLine();
        sb.AppendLine(BindingPolicies(morgana));
        sb.AppendLine();
        sb.AppendLine(MorganaLayerFooter);
        sb.AppendLine();
        
        // Alembic. Three rows read as one: what holds in every interview, which of the two jobs this
        // step is and what this interviewer alone settles — in that order, because the mode governs
        // how the interviewer's own instructions are to be read.
        Records.Prompt interviewer = Resolve(interviewerId);
        Records.Prompt alembic = Resolve(AlembicPromptId);
        Records.Prompt mode = Resolve(correcting ? CorrectingPromptId : ComposingPromptId);

        sb.AppendLine(AlembicLayerHeader);
        sb.AppendLine();
        AppendSection(sb, alembic.Target, mode.Target, interviewer.Target);
        AppendSection(sb, alembic.Personality, mode.Personality, interviewer.Personality);
        AppendSection(sb, alembic.Instructions, mode.Instructions, interviewer.Instructions);
        AppendSection(sb, alembic.Formatting, mode.Formatting, interviewer.Formatting);

        return sb.ToString();
    }

    /// <summary>
    /// The two policies where the exception below applies: their MECHANIC is fixed above the agent,
    /// but which of a domain's own moments deserve one is not something the framework can know —
    /// only the interview, having just heard the domain, can say whether a decision point is a
    /// closed set of actions worth buttons, or a tool's output structured enough to want a card.
    /// </summary>
    private static readonly string[] ExpressivenessPolicies = ["QuickReplyDoctrine", "RichCardUsage"];

    /// <summary>
    /// Names the policies already binding on every agent Alembic writes.
    /// </summary>
    /// <remarks>
    /// Names only, for every policy except the two named in <see cref="ExpressivenessPolicies"/>.
    /// What Alembic has to know for the rest is which subjects are settled above the agent, so that
    /// it writes none of them again; how they are settled is the agent's business at runtime and
    /// not the author's and the bodies run to some 14 000 characters of turn mechanics Alembic has
    /// no turn to apply them to.
    /// </remarks>
    private static string BindingPolicies(Records.Prompt morgana)
    {
        List<Records.GlobalPolicy> policies =
            morgana.GetAdditionalPropertyOrDefault<List<Records.GlobalPolicy>>("GlobalPolicies", []);

        if (policies.Count == 0)
            return string.Empty;

        string[] silent = [.. policies.Select(p => p.Name).Where(n => !ExpressivenessPolicies.Contains(n))];
        string[] expressive = [.. policies.Select(p => p.Name).Where(n => ExpressivenessPolicies.Contains(n))];

        StringBuilder sb = new StringBuilder();

        if (silent.Length > 0)
        {
            sb.Append("ALREADY BINDING on every agent written here, stated above it and with more authority: ")
              .Append(string.Join(", ", silent))
              .Append(". Never write a rule about any of these subjects into an agent's own prose.");
        }

        if (expressive.Length > 0)
        {
            if (sb.Length > 0)
                sb.Append(' ');

            sb.Append("ALSO ALREADY BINDING, but not silently: ")
              .Append(string.Join(", ", expressive))
              .Append(". Their MECHANIC is fixed above the agent and never yours to restate — but which of ")
              .Append("this domain's own moments earns a closed set of buttons, or a card, is exactly what an ")
              .Append("agent's own author is expected to say. Where the agent being written has one, name it, ")
              .Append("in its own Formatting, the way a hand-authored agent already does.");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends one section of the composed prompt: what Alembic always says under this label, then
    /// what this interviewer adds under it.
    /// </summary>
    /// <param name="sb">The prompt being built.</param>
    /// <param name="parts">
    /// This section's rows in reading order: what Alembic says in every interview, which carries the
    /// label; then which of the two jobs this step is; then what this interviewer adds. Only the
    /// first is labelled — the rest fall under it, which is what makes the three read as one section.
    /// </param>
    private static void AppendSection(StringBuilder sb, params string?[] parts)
    {
        foreach (string? part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
                continue;

            sb.AppendLine(part.Trim());
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Reads <c>alembic.json</c> out of this assembly.
    /// </summary>
    private static Records.Prompt[] LoadAlembicPrompts()
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
