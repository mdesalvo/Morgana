using System.Reflection;
using System.Text;
using System.Text.Json;
using Alembic.Interfaces;
using Morgana.AI;
using Morgana.AI.Adapters;
using Morgana.AI.Interfaces;
using Morgana.AI.Services;

namespace Alembic.Services;

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
    // twice with nothing saying which is which. Their texts are the Alembic prompt's PromptInjections.
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

    /// <summary>
    /// The placeholder of a text of <c>alembic.json</c>, written <c>((name))</c> there.
    /// </summary>
    private static string Placeholder(string name) => $"(({name}))";

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
        sb.AppendLine(ResolvePromptInjection(AlembicPromptId, "MorganaLayerHeader"));
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Target, morgana.Target));
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Personality, morgana.Personality));
        sb.AppendLine();
        sb.AppendLine(await ComposeFrameworkPrimerAsync());
        sb.AppendLine();
        sb.AppendLine(ResolvePromptInjection(AlembicPromptId, "MorganaLayerFooter"));
        sb.AppendLine();
        
        // Alembic. Three rows read as one: what holds in every interview, which of the two jobs this
        // step is and what this interviewer alone settles — in that order, because the mode governs
        // how the interviewer's own instructions are to be read.
        Records.Prompt interviewer = Resolve(interviewerId);
        Records.Prompt alembic = Resolve(AlembicPromptId);
        Records.Prompt mode = Resolve(correcting ? CorrectingPromptId : ComposingPromptId);

        sb.AppendLine(ResolvePromptInjection(AlembicPromptId, "AlembicLayerHeader"));
        sb.AppendLine();
        AppendSection(sb, Constants.SectionLabels.Target, alembic.Target, mode.Target, interviewer.Target);
        AppendSection(sb, Constants.SectionLabels.Personality, alembic.Personality, mode.Personality, interviewer.Personality);
        AppendSection(sb, Constants.SectionLabels.Instructions, alembic.Instructions, mode.Instructions, interviewer.Instructions);
        AppendSection(sb, Constants.SectionLabels.Formatting, alembic.Formatting, mode.Formatting, interviewer.Formatting);

        return sb.ToString();
    }

    /// <inheritdoc />
    public string ResolvePromptInjection(string promptId, string injectionName, params (string Placeholder, string Value)[] values)
    {
        // The injections of the prompt that was asked for: a prompt that declares none is an authoring defect and not an empty set.
        Records.Prompt prompt = Resolve(promptId);
        string text = Records.Injection.ResolveTemplate(
            prompt.GetAdditionalProperty<List<Records.Injection>>(Constants.PromptProperties.PromptInjections), injectionName);

        return Spliced(text, $"The {promptId} prompt in alembic.json declares no '{injectionName}' injection.", values);
    }

    /// <inheritdoc />
    public string ResolveToolInjection(string injectionName, params (string Placeholder, string Value)[] values)
    {
        // One list answers for both tool classes of Alembic, since they share their helpers.
        Records.Prompt prompt = Resolve(AlembicPromptId);
        string text = Records.Injection.ResolveTemplate(
            prompt.GetAdditionalProperty<List<Records.Injection>>(Constants.PromptProperties.ToolInjections), injectionName);

        return Spliced(text, $"The {AlembicPromptId} prompt in alembic.json declares no '{injectionName}' tool injection.", values);
    }

    /// <summary>
    /// Fills the placeholders of an injection's text with the values of the call.
    /// </summary>
    /// <param name="text">The injection as authored; empty when the prompt declares none.</param>
    /// <param name="absence">What the exception says when the text is empty.</param>
    /// <param name="values">Placeholder name to the value that it stands for.</param>
    private static string Spliced(string text, string absence, (string Placeholder, string Value)[] values)
    {
        // A text that is absent stops here: the model handed a request with a sentence missing would work wrong without any sign of it.
        if (text.Length == 0)
            throw new InvalidOperationException(absence);

        // Each placeholder is replaced literally: the values come from the turn and must never be read as patterns.
        foreach ((string placeholder, string value) in values)
            text = text.Replace(Placeholder(placeholder), value, StringComparison.Ordinal);

        // The sentence as the model reads it.
        return text;
    }

    /// <inheritdoc />
    /// <returns>The framework as fact: how a message finds its agent, how a turn is formed around
    /// it, what the runtime splices into the prose written here, what every agent can already do and
    /// what each of Morgana's policies settles above every agent.</returns>
    public async Task<string> ComposeFrameworkPrimerAsync()
    {
        Records.Prompt morgana = await morganaPrompt.Value;

        StringBuilder sb = new StringBuilder();
        sb.AppendLine(ResolvePromptInjection(AlembicPromptId, "MorganaPrimerHeader"));
        sb.AppendLine();
        sb.AppendLine(Resolve(MorganaPrimerPromptId).Target);
        sb.AppendLine();
        sb.AppendLine(BindingPolicies(morgana));
        sb.AppendLine(ResolvePromptInjection(AlembicPromptId, "MorganaPrimerFooter"));

        return sb.ToString().TrimEnd();
    }

    /// <inheritdoc />
    public MorganaToolAdapter OfferTools(Records.WorkflowStep step, object toolInstance)
    {
        MorganaToolAdapter toolAdapter = new MorganaToolAdapter();
        Type toolClass = toolInstance.GetType();
        IReadOnlyList<MethodInfo> methods = ProvidesToolForIntentRegistryService.GetToolMethods(toolClass);
        IReadOnlyList<Records.ToolDefinition> catalog = ProvidesToolForIntentRegistryService.ProjectToolDefinitions(toolClass);

        foreach (string toolName in step.Tools)
        {
            // A name that the class does not declare is refused here so that the model is never offered a tool nothing implements.
            MethodInfo method = methods.FirstOrDefault(candidate => candidate.Name == toolName)
                ?? throw new InvalidOperationException(
                    $"Step '{step.Name}' offers tool '{toolName}', but {toolClass.Name} has no tool by that name.");

            Delegate implementation = Delegate.CreateDelegate(
                System.Linq.Expressions.Expression.GetDelegateType(
                [
                    .. method.GetParameters().Select(parameter => parameter.ParameterType),
                    method.ReturnType
                ]),
                toolInstance,
                method);

            toolAdapter.AddTool(toolName, implementation, catalog.First(candidate => candidate.Name == toolName));
        }

        return toolAdapter;
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
        string[] stale = [.. glosses.Keys.Where(name => policies.All(policy => policy.Name != name))];

        if (unglossed.Length > 0 || stale.Length > 0)
            throw new InvalidOperationException(
                $"The {MorganaPrimerPromptId} prompt no longer matches morgana.json's policies — "
                + $"missing a line for: {string.Join(", ", unglossed)}; carrying a line for policies that no longer exist: {string.Join(", ", stale)}.");

        string[] silent = [.. policies.Select(policy => policy.Name).Where(name => !ExpressivenessPolicies.Contains(name))];
        string[] expressive = [.. policies.Select(policy => policy.Name).Where(name => ExpressivenessPolicies.Contains(name))];

        StringBuilder sb = new StringBuilder();

        if (silent.Length > 0)
        {
            sb.AppendLine(ResolvePromptInjection(AlembicPromptId, "BindingPoliciesSilent"));
            AppendGlossed(sb, silent, glosses);
        }

        if (expressive.Length > 0)
        {
            sb.AppendLine(ResolvePromptInjection(AlembicPromptId, "BindingPoliciesExpressive"));
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
    private static void AppendSection(StringBuilder sb, string label, params string?[] parts)
    {
        // The three rows read as one section under one label: the model sees no seam between them.
        string section = Records.Prompt.Labeled(label,
            string.Join("\n\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim())));
        if (section.Length == 0)
            return;

        sb.AppendLine(section);
        sb.AppendLine();
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
