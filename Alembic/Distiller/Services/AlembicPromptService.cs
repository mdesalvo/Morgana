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

    /// <summary>
    /// The prompt holding what Alembic knows about the framework its agents will run under: how a
    /// message reaches one, how a turn is formed around it, what the runtime splices into the prose
    /// written here and what every agent can already do without a tool being declared for it.
    /// </summary>
    /// <remarks>
    /// Authored here rather than read out of <c>morgana.json</c> and that is the whole point of it.
    /// The framework's own rules are written in the imperative to an agent taking a turn; handed
    /// over as they stand they are orders Alembic has no turn to carry out, which is the way to
    /// manufacture exactly the non-local contradictions this project exists to avoid. Restated in
    /// the descriptive third person the same facts stop being orders and become knowledge of the
    /// world the authored agents will live in, which is what an author needs and never had.
    /// </remarks>
    private const string MorganaPrimerPromptId = "MorganaPrimer";

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

    // The one place in the fence that is not her voice. Everything above it is Morgana speaking and
    // binding; what follows is the framework described so an author can write against it. An author
    // reading it as one more thing to obey would start writing turn machinery into an agent's own
    // prose — the exact defect the primer exists to prevent.
    private const string MorganaPrimerHeader =
        "-------- HOW SHE RUNS WHAT YOU WRITE --------\n" +
        "Fact about the world your agents will live in, not instruction to you.";

    // The primer closes on its own mark because two of its readers stand outside the fence: the
    // coherence pass and the one that applies its findings get the framework without Morgana's
    // voice, and an unterminated block of world facts would run straight into their own prose.
    private const string MorganaPrimerFooter = "-------- END OF WHAT SHE ALREADY DOES --------";
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
        sb.AppendLine(await ComposeFrameworkPrimerAsync());
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

    /// <inheritdoc />
    /// <returns>The framework as fact: how a message finds its agent, how a turn is formed around
    /// it, what the runtime splices into the prose written here, what every agent can already do and
    /// what each of Morgana's policies settles above every agent.</returns>
    public async Task<string> ComposeFrameworkPrimerAsync()
    {
        Records.Prompt morgana = await morganaPrompt.Value;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine(MorganaPrimerHeader);
        sb.AppendLine();
        sb.AppendLine(Resolve(MorganaPrimerPromptId).Target);
        sb.AppendLine();
        sb.AppendLine(BindingPolicies(morgana));
        sb.AppendLine(MorganaPrimerFooter);

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The two policies where the exception below applies: their MECHANIC is fixed above the agent,
    /// but which of a domain's own moments deserve one is not something the framework can know —
    /// only the interview, having just heard the domain, can say whether a decision point is a
    /// closed set of actions worth buttons, or a tool's output structured enough to want a card.
    /// </summary>
    private static readonly string[] ExpressivenessPolicies = ["QuickReplyDoctrine", "RichCardUsage"];

    /// <summary>
    /// States the policies already binding on every agent Alembic writes, each under the one line
    /// the primer gives it.
    /// </summary>
    /// <remarks>
    /// The names are <c>morgana.json</c>'s, which is what makes them authoritative; the line beside
    /// each is Alembic's own reading of that policy, addressed to whoever is writing an agent. A
    /// bare list of names was the whole of this block once and it forbade subjects the model could
    /// not name: a prohibition on <c>ToolGrounding</c> is unenforceable by a reader who has never
    /// been told what <c>ToolGrounding</c> settles. The bodies still stay out — 14 000 characters
    /// of turn mechanics for a process that takes no turn.
    /// </remarks>
    private string BindingPolicies(Records.Prompt morgana)
    {
        List<Records.GlobalPolicy> policies =
            morgana.GetAdditionalPropertyOrDefault<List<Records.GlobalPolicy>>(Constants.PromptProperties.GlobalPolicies, []);

        if (policies.Count == 0)
            return string.Empty;

        Dictionary<string, string> glosses = Glosses();

        // A policy added to the framework and not to the primer would reach the author as a name
        // with nothing behind it, which is the state this block was rebuilt to leave; one glossed
        // here and since removed from the framework teaches a rule that no longer binds. Both are
        // authoring defects nothing downstream can notice, so both stop the interview here.
        string[] unglossed = [.. policies.Select(policy => policy.Name).Where(name => !glosses.ContainsKey(name))];
        string[] stale = [.. glosses.Keys.Where(name => !policies.Any(policy => policy.Name == name))];

        if (unglossed.Length > 0 || stale.Length > 0)
            throw new InvalidOperationException(
                $"The {MorganaPrimerPromptId} prompt no longer matches morgana.json's policies — "
                + $"missing a line for: {string.Join(", ", unglossed)}; carrying a line for policies that no longer exist: {string.Join(", ", stale)}.");

        string[] silent = [.. policies.Select(policy => policy.Name).Where(name => !ExpressivenessPolicies.Contains(name))];
        string[] expressive = [.. policies.Select(policy => policy.Name).Where(name => ExpressivenessPolicies.Contains(name))];

        StringBuilder sb = new StringBuilder();

        if (silent.Length > 0)
        {
            sb.AppendLine("ALREADY BINDING on every agent written here, stated above it and with more authority. Never write a rule about any of these subjects into an agent's own prose — what each of them already settles:");
            AppendGlossed(sb, silent, glosses);
        }

        if (expressive.Length > 0)
        {
            sb.AppendLine("ALSO ALREADY BINDING, but not silently. Their MECHANIC is fixed above the agent and never yours to restate, while what does belong to an agent's own author is named in each:");
            AppendGlossed(sb, expressive, glosses);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Lists policies one per line, each carrying what it settles.
    /// </summary>
    private static void AppendGlossed(StringBuilder sb, string[] names, Dictionary<string, string> glosses)
    {
        foreach (string name in names)
            sb.Append("- ").Append(name).Append(" — ").AppendLine(glosses[name]);

        sb.AppendLine();
    }

    /// <summary>
    /// What each framework policy settles, said once for an author rather than for an agent taking
    /// a turn, keyed by the policy's own name in <c>morgana.json</c>.
    /// </summary>
    private Dictionary<string, string> Glosses() =>
        Resolve(MorganaPrimerPromptId)
            .GetAdditionalPropertyOrDefault<List<Records.GlobalPolicy>>(Constants.PromptProperties.GlobalPolicies, [])
            .ToDictionary(policy => policy.Name, policy => policy.Description);

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
