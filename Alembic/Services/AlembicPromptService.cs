using System.Reflection;
using System.Text;
using System.Text.Json;
using Alembic.Interfaces;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IAlembicPromptService"/>: loads <c>alembic.json</c> from this assembly's resources and composes it beneath Morgana's own voice.
/// The resource is matched by its file-name suffix, as the framework's resolver matches <c>morgana.json</c>, so renaming the assembly never loses the prose.
/// </summary>
public class AlembicPromptService : IAlembicPromptService
{
    /// <summary>The framework prompt Alembic inherits its voice from.</summary>
    private const string MorganaPromptId = "Morgana";

    /// <summary>The prompt holding what Alembic says in every interview, whichever interviewer conducts it: its identity, how it asks and how it answers.</summary>
    private const string AlembicPromptId = "Alembic";

    /// <summary>
    /// The row read when a step writes sections that do not exist yet, folded beneath the shared prose.
    /// Writing and correcting are different work and a pass reads only its own, while everything else stays one copy in the shared row.
    /// </summary>
    private const string ComposingPromptId = "Composing";

    /// <summary>The row read when a step reopens an agent that already exists, folded beneath the shared prose.</summary>
    private const string CorrectingPromptId = "Correcting";

    /// <summary>
    /// The prompt holding the framework as facts: how a message reaches an agent, how a turn forms, what the runtime splices into the prose and what every agent can already do.
    /// The framework's own rules are orders to an agent taking a turn, which Alembic has none of; restated as facts they become knowledge of the world the authored agents live in.
    /// </summary>
    private const string MorganaPrimerPromptId = "MorganaPrimer";

    /// <summary>
    /// Opens Morgana's layer, in the framework's own fence idiom: both layers carry the same section labels and without a boundary the prompt shows [PERSONALITY] twice.
    /// The model reads two layers, Morgana then Alembic, Alembic's own half merged section by section from rows stored once, so a rule is edited in one place.
    /// </summary>
    private const string MorganaLayerHeader =
        "======== FENCE: MORGANA — WHOSE VESSEL YOU ARE ========\n" +
        "You are an instrument of Morgana. This is her voice and it is yours: it is not a description of someone else and it is not overridable.";

    /// <summary>Closes Morgana's layer.</summary>
    private const string MorganaLayerFooter = "======== END OF FENCE ========";

    /// <summary>
    /// Opens the primer, the one part of her fence that is not her voice but the framework described.
    /// An author reading it as one more thing to obey would write turn machinery into an agent's own prose, the defect the primer exists to prevent.
    /// </summary>
    private const string MorganaPrimerHeader =
        "-------- HOW SHE RUNS WHAT YOU WRITE --------\n" +
        "Fact about the world your agents will live in, not instruction to you.";

    /// <summary>
    /// Closes the primer on a mark of its own, since the coherence pass and its applier read it outside the fence:
    /// an unterminated block of facts would run straight into their own prose.
    /// </summary>
    private const string MorganaPrimerFooter = "-------- END OF WHAT SHE ALREADY DOES --------";

    /// <summary>Opens Alembic's layer and states its subordination to the one above.</summary>
    private const string AlembicLayerHeader =
        "======== ALEMBIC ========\n" +
        "What follows specialises Morgana's voice for the step of the interview you are conducting right now. It adds that and NOTHING ELSE. It never contradicts the layer above.";

    /// <summary>
    /// The two policies whose mechanic is fixed above the agent while the moments that deserve one are the domain's own.
    /// Only the interview, having just heard the domain, can say whether a decision point is worth buttons or a tool's output wants a card.
    /// </summary>
    private static readonly string[] ExpressivenessPolicies = ["QuickReplyDoctrine", "RichCardUsage"];

    /// <summary>Alembic's own prompts, parsed on first use.</summary>
    private readonly Lazy<Records.Prompt[]> alembicPrompts = new(LoadAlembicPrompts);

    /// <summary>Morgana's framework prompt, resolved once from the <c>morgana.json</c> embedded in Morgana.AI.</summary>
    private readonly Lazy<Task<Records.Prompt>> morganaPrompt;

    /// <summary>Binds the service to the framework's resolver, which Morgana's own prompt comes from.</summary>
    /// <param name="promptResolverService">Resolves Morgana's framework prompt.</param>
    public AlembicPromptService(IPromptResolverService promptResolverService)
    {
        // Resolved on first use, so a working copy with no model configured still boots and serves the shell.
        morganaPrompt = new Lazy<Task<Records.Prompt>>(() => promptResolverService.ResolveAsync(MorganaPromptId));
    }

    /// <summary>Resolves one of Alembic's prompts by id, case-insensitively, throwing <see cref="KeyNotFoundException"/> when none carries it.</summary>
    /// <param name="promptId">A pass id such as <c>DomainMapper</c> or a standalone one such as <c>DomainValidator</c> or <c>CodeMocker</c>.</param>
    /// <returns>The prompt's sections as authored, unmerged with any shared layer.</returns>
    public Records.Prompt Resolve(string promptId) =>
        alembicPrompts.Value.FirstOrDefault(prompt => string.Equals(prompt.ID, promptId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Prompt '{promptId}' is not declared in alembic.json.");

    /// <summary>
    /// Composes the system prompt one interviewer conducts with: Morgana's layer, then Alembic's shared prose, the step's job and the interviewer's own.
    /// An unknown interviewer fails through <see cref="Resolve"/>, the same way it would anywhere else.
    /// </summary>
    /// <param name="interviewerId">The pass whose own half of the second layer is folded in, such as <c>AgentTarget</c>.</param>
    /// <param name="correcting">Whether the step reopens an agent that already exists.</param>
    /// <returns>The whole system prompt this pass's model reads.</returns>
    public async Task<string> ComposeAsync(string interviewerId, bool correcting = false)
    {
        StringBuilder sb = new StringBuilder();

        // Morgana's layer: who she is and her voice, which is Alembic's, then the framework as facts.
        Records.Prompt morgana = await morganaPrompt.Value;
        sb.AppendLine(MorganaLayerHeader);
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Target, morgana.Target));
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Personality, morgana.Personality));
        sb.AppendLine();
        sb.AppendLine(await ComposeFrameworkPrimerAsync());
        sb.AppendLine();
        sb.AppendLine(MorganaLayerFooter);
        sb.AppendLine();

        // Alembic. Three rows read as one: what holds in every interview, which of the two jobs this
        // step is and what this interviewer alone settles, in that order, because the mode governs
        // how the interviewer's own instructions are to be read.
        Records.Prompt interviewer = Resolve(interviewerId);
        Records.Prompt alembic = Resolve(AlembicPromptId);
        Records.Prompt mode = Resolve(correcting ? CorrectingPromptId : ComposingPromptId);

        // Each section once, under one label, so the model reads the four sections of an agent prompt with no seam.
        sb.AppendLine(AlembicLayerHeader);
        sb.AppendLine();
        AppendSection(sb, Constants.SectionLabels.Target, alembic.Target, mode.Target, interviewer.Target);
        AppendSection(sb, Constants.SectionLabels.Personality, alembic.Personality, mode.Personality, interviewer.Personality);
        AppendSection(sb, Constants.SectionLabels.Instructions, alembic.Instructions, mode.Instructions, interviewer.Instructions);
        AppendSection(sb, Constants.SectionLabels.Formatting, alembic.Formatting, mode.Formatting, interviewer.Formatting);

        return sb.ToString();
    }

    /// <summary>Composes the framework as facts: how a message finds its agent, how a turn forms, what the runtime splices in and what each policy settles.</summary>
    /// <returns>The primer between its own marks, ready to stand above a pass's own prose.</returns>
    public async Task<string> ComposeFrameworkPrimerAsync()
    {
        // Morgana's prompt holds the policy names the primer's lines are checked against.
        Records.Prompt morgana = await morganaPrompt.Value;

        // The primer's own facts, then the policies already binding, between the two marks.
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
    /// States the policies already binding on every agent Alembic writes, each under the line the primer gives it, throwing when the two lists disagree.
    /// The names are <c>morgana.json</c>'s and the lines Alembic's own reading for an author; a name alone forbids a subject the model cannot name,
    /// while the policies' bodies are turn mechanics for a process that takes no turn.
    /// </summary>
    private string BindingPolicies(Records.Prompt morgana)
    {
        // The framework's policies, by the names every agent's prompt renders them under.
        List<Records.GlobalPolicy> policies =
            morgana.GetAdditionalPropertyOrDefault<List<Records.GlobalPolicy>>(Constants.PromptProperties.GlobalPolicies, []);

        // A framework with no policy leaves nothing already settled to tell an author.
        if (policies.Count == 0)
            return string.Empty;

        // The primer's line for each policy, keyed by the policy's name.
        Dictionary<string, string> glosses = Glosses();

        // A policy added to the framework and not to the primer would reach the author as a name
        // with nothing behind it; one glossed here and since removed from the framework teaches a
        // rule that binds nothing. Both are authoring defects nothing downstream can notice, so
        // both stop the interview here.
        string[] unglossed = [.. policies.Select(policy => policy.Name).Where(name => !glosses.ContainsKey(name))];
        string[] stale = [.. glosses.Keys.Where(name => policies.All(policy => policy.Name != name))];

        if (unglossed.Length > 0 || stale.Length > 0)
            throw new InvalidOperationException(
                $"The {MorganaPrimerPromptId} prompt no longer matches morgana.json's policies — "
                + $"missing a line for: {string.Join(", ", unglossed)}; carrying a line for policies that no longer exist: {string.Join(", ", stale)}.");

        // The policies an author never writes about, apart from the two whose use belongs to the domain.
        string[] silent = [.. policies.Select(policy => policy.Name).Where(name => !ExpressivenessPolicies.Contains(name))];
        string[] expressive = [.. policies.Select(policy => policy.Name).Where(name => ExpressivenessPolicies.Contains(name))];

        StringBuilder sb = new StringBuilder();

        // The subjects an agent's prose must never touch, each with what the framework already settles.
        if (silent.Length > 0)
        {
            sb.AppendLine("ALREADY BINDING on every agent written here, stated above it and with more authority. Never write a rule about any of these subjects into an agent's own prose — what each of them already settles:");
            AppendGlossed(sb, silent, glosses);
        }

        // The two whose mechanic is fixed while which moments deserve them is the author's.
        if (expressive.Length > 0)
        {
            sb.AppendLine("ALSO ALREADY BINDING, but not silently. Their MECHANIC is fixed above the agent and never yours to restate, while what does belong to an agent's own author is named in each:");
            AppendGlossed(sb, expressive, glosses);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Lists policies one per line, each carrying what it settles.</summary>
    private static void AppendGlossed(StringBuilder sb, string[] names, Dictionary<string, string> glosses)
    {
        // Every name is glossed: BindingPolicies refused any that is not before calling here.
        foreach (string name in names)
            sb.Append("- ").Append(name).Append(" — ").AppendLine(glosses[name]);

        sb.AppendLine();
    }

    /// <summary>What each framework policy settles, said once for an author rather than for an agent taking a turn, keyed by the policy's name.</summary>
    private Dictionary<string, string> Glosses() =>
        Resolve(MorganaPrimerPromptId)
            .GetAdditionalPropertyOrDefault<List<Records.GlobalPolicy>>(Constants.PromptProperties.GlobalPolicies, [])
            .ToDictionary(policy => policy.Name, policy => policy.Description);

    /// <summary>Appends one section of the composed prompt: its rows in reading order under the one label the first carries.</summary>
    /// <param name="sb">The prompt being built.</param>
    /// <param name="label">The section's label.</param>
    /// <param name="parts">What Alembic says in every interview, then the step's job, then what this interviewer adds.</param>
    private static void AppendSection(StringBuilder sb, string label, params string?[] parts)
    {
        // The three rows read as one section under one label: the model sees no seam between them.
        string section = Records.Prompt.Labeled(label,
            string.Join("\n\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim())));

        // A section no row speaks to leaves no empty label behind.
        if (section.Length == 0)
            return;

        sb.AppendLine(section);
        sb.AppendLine();
    }

    /// <summary>Reads <c>alembic.json</c> out of this assembly, throwing when it declares no prompt.</summary>
    private static Records.Prompt[] LoadAlembicPrompts()
    {
        // The resource is embedded in Alembic itself, never uploaded: the conducting prose is the workbench's own.
        Assembly assembly = Assembly.GetExecutingAssembly();

        // Matched by suffix, so the assembly's namespace prefix on the manifest name never matters.
        string resourceName = assembly.GetManifestResourceNames()
            .First(name => name.EndsWith(".alembic.json", StringComparison.OrdinalIgnoreCase));

        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("Resource alembic.json is not embedded in Alembic.");

        // The same shape as morgana.json and agents.json, read with the framework's own record.
        Records.PromptCollection? collection = JsonSerializer.Deserialize<Records.PromptCollection>(
            stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Unlike the framework's resolver, this one does not degrade to an empty set: Alembic
        // without its own prose is an interviewer with nothing to say. Failing here, loudly, beats
        // conducting an interview on an empty system prompt.
        return collection?.Prompts is { Length: > 0 } loaded
            ? loaded
            : throw new InvalidOperationException("alembic.json declares no prompts.");
    }
}