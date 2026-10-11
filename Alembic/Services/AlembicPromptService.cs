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
/// Composes the prompts of Alembic's passes from <c>alembic.json</c> beneath Morgana's own voice and the framework primer.
/// </summary>
/// <remarks>The file is matched by its name suffix, so the prose survives a rename of the assembly.</remarks>
public class AlembicPromptService : IAlembicPromptService
{
    /// <summary>The <c>morgana.json</c> prompt whose Target and Personality open every composed pass.</summary>
    private const string MorganaPromptId = "Morgana";

    /// <summary>The prompt holding what Alembic says in every pass: its identity, how a Morgana domain runs, how it asks and how it answers.</summary>
    private const string AlembicPromptId = "Alembic";

    /// <summary>The prompt row read by a step that writes a section that does not exist yet.</summary>
    private const string ComposingPromptId = "Composing";

    /// <summary>The prompt row read by a step that corrects a section that already exists.</summary>
    private const string CorrectingPromptId = "Correcting";

    /// <summary>The prompt that describes the framework to an author: how a message finds its agent, how a turn is formed, what the runtime splices in and what every agent can already do.</summary>
    /// <remarks>It restates the framework's imperative rules in the descriptive third person, as facts Alembic has no turn to obey.</remarks>
    private const string MorganaPrimerPromptId = "MorganaPrimer";

    /// <summary>Writes a placeholder name the way the texts of <c>alembic.json</c> carry it, <c>((name))</c>.</summary>
    private static string Placeholder(string name) => $"(({name}))";

    /// <summary>Alembic's own prompts, parsed on the first call that needs them.</summary>
    private readonly Lazy<Records.Prompt[]> alembicPrompts = new(LoadAlembicPrompts);

    /// <summary>Morgana's own prompt from the embedded <c>morgana.json</c>, whose voice and policies Alembic's passes inherit.</summary>
    private readonly Lazy<Task<Records.Prompt>> morganaPrompt;

    /// <summary>
    /// Initializes the service over the resolver of Morgana's own prompt.
    /// </summary>
    public AlembicPromptService(IPromptResolverService promptResolverService)
    {
        morganaPrompt = new Lazy<Task<Records.Prompt>>(() => promptResolverService.ResolveAsync(MorganaPromptId));
    }

    /// <inheritdoc />
    public Records.Prompt Resolve(string promptId) =>
        alembicPrompts.Value.FirstOrDefault(p => string.Equals(p.ID, promptId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Prompt '{promptId}' is not declared in alembic.json.");

    /// <inheritdoc />
    /// <param name="interviewerId">The pass whose own prose is folded into the shared one.</param>
    /// <param name="correcting">Whether the step corrects an existing section instead of writing a new one.</param>
    public async Task<string> ComposeAsync(string interviewerId, bool correcting = false)
    {
        // The composed prompt is read as one text by the interviewing model.
        StringBuilder sb = new StringBuilder();

        // The first layer is Morgana in her own words: her Target and Personality read live from morgana.json.
        Records.Prompt morgana = await morganaPrompt.Value;
        sb.AppendLine(ResolvePromptInjection(AlembicPromptId, "MorganaLayerHeader"));
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Target, morgana.Target));
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Personality, morgana.Personality));
        sb.AppendLine();
        // The framework primer closes the first layer, so the pass knows the rules that bind the agents it writes.
        sb.AppendLine(await ComposeFrameworkPrimerAsync());
        sb.AppendLine();
        sb.AppendLine(ResolvePromptInjection(AlembicPromptId, "MorganaLayerFooter"));
        sb.AppendLine();

        // The second layer is three rows read as one: what holds in every pass then the job of this step then what this pass alone settles.
        // The job comes before the pass's own prose because it governs how that prose is read.
        Records.Prompt interviewer = Resolve(interviewerId);
        Records.Prompt alembic = Resolve(AlembicPromptId);
        Records.Prompt mode = Resolve(correcting ? CorrectingPromptId : ComposingPromptId);

        // The rows are merged section by section under one set of labels, so the model reads four sections with no seam.
        sb.AppendLine(ResolvePromptInjection(AlembicPromptId, "AlembicLayerHeader"));
        sb.AppendLine();
        AppendSection(sb, Constants.SectionLabels.Target, alembic.Target, mode.Target, interviewer.Target);
        AppendSection(sb, Constants.SectionLabels.Personality, alembic.Personality, mode.Personality, interviewer.Personality);
        AppendSection(sb, Constants.SectionLabels.Instructions, alembic.Instructions, mode.Instructions, interviewer.Instructions);
        AppendSection(sb, Constants.SectionLabels.Formatting, alembic.Formatting, mode.Formatting, interviewer.Formatting);

        // The whole system prompt of the pass.
        return sb.ToString();
    }

    /// <inheritdoc />
    public string ResolvePromptInjection(string promptId, string injectionName, params (string Placeholder, string Value)[] values)
    {
        // The injections of the prompt that was asked for: a prompt that declares none is an authoring defect and not an empty set.
        Records.Prompt prompt = Resolve(promptId);
        string text = Records.Injection.ResolveTemplate(
            prompt.GetAdditionalProperty<List<Records.Injection>>(Constants.PromptProperties.PromptInjections), injectionName);

        // The injection reaches the model with its placeholders filled by the values of this call.
        return Spliced(text, $"The {promptId} prompt in alembic.json declares no '{injectionName}' injection.", values);
    }

    /// <inheritdoc />
    public string ResolveToolInjection(string injectionName, params (string Placeholder, string Value)[] values)
    {
        // One list answers for both tool classes of Alembic, since they share their helpers.
        Records.Prompt prompt = Resolve(AlembicPromptId);
        string text = Records.Injection.ResolveTemplate(
            prompt.GetAdditionalProperty<List<Records.Injection>>(Constants.PromptProperties.ToolInjections), injectionName);

        // The tool injection reaches the model with its placeholders filled by the values of this call.
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
    public async Task<string> ComposeFrameworkPrimerAsync()
    {
        // The policies are read live from morgana.json, so the primer names exactly those that bind.
        Records.Prompt morgana = await morganaPrompt.Value;

        // The primer is a header, the framework described to an author, one line per binding policy and a footer.
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
        // The adapter collects the tools of the step in the form the framework offers them to a model.
        MorganaToolAdapter toolAdapter = new MorganaToolAdapter();

        // The tool class is read with the framework's own catalog, so its declarations are the ones an agent's would be.
        Type toolClass = toolInstance.GetType();
        IReadOnlyList<MethodInfo> methods = ProvidesToolForIntentRegistryService.GetToolMethods(toolClass);
        IReadOnlyList<Records.ToolDefinition> catalog = ProvidesToolForIntentRegistryService.ProjectToolDefinitions(toolClass);

        // The step offers exactly the tools it names: what a pass may write is which tools exist.
        foreach (string toolName in step.Tools)
        {
            // A name that the class does not declare is refused here so that the model is never offered a tool nothing implements.
            MethodInfo method = methods.FirstOrDefault(candidate => candidate.Name == toolName)
                ?? throw new InvalidOperationException(
                    $"Step '{step.Name}' offers tool '{toolName}', but {toolClass.Name} has no tool by that name.");

            // The tool method is bound to this instance, which holds the draft the tool writes into.
            Delegate implementation = Delegate.CreateDelegate(
                System.Linq.Expressions.Expression.GetDelegateType(
                [
                    .. method.GetParameters().Select(parameter => parameter.ParameterType),
                    method.ReturnType
                ]),
                toolInstance,
                method);

            // The tool is offered with the declaration its method carries.
            toolAdapter.AddTool(toolName, implementation, catalog.First(candidate => candidate.Name == toolName));
        }

        // The step's tools, ready for the pass's model.
        return toolAdapter;
    }

    /// <summary>The policies whose mechanic is fixed by the framework but whose use the interview decides, since only the domain says which moments earn a button or a card.</summary>
    private static readonly string[] ExpressivenessPolicies = ["QuickReplyDoctrine", "RichCardUsage"];

    /// <summary>
    /// States the policies already binding on every agent Alembic writes, each under the one line
    /// the primer gives it.
    /// </summary>
    /// <remarks>The names are <c>morgana.json</c>'s while each line is Alembic's reading of the policy for an author.</remarks>
    private string BindingPolicies(Records.Prompt morgana)
    {
        // The policies that morgana.json binds on every agent.
        List<Records.GlobalPolicy> policies =
            morgana.GetAdditionalPropertyOrDefault<List<Records.GlobalPolicy>>(Constants.PromptProperties.GlobalPolicies, []);

        // A framework with no policies leaves the block empty.
        if (policies.Count == 0)
            return string.Empty;

        // The primer's line for each policy, keyed by the policy's own name.
        Dictionary<string, string> glosses = Glosses();

        // A policy without a line reaches the author as a name with nothing behind it and a line without a policy teaches a rule that does not bind.
        // Nothing downstream can notice either, so both stop the interview here.
        string[] glosslessPolicies = [.. policies.Select(policy => policy.Name).Where(name => !glosses.ContainsKey(name))];
        string[] strayGlosses = [.. glosses.Keys.Where(name => policies.All(policy => policy.Name != name))];

        // The two sides disagree: the interview is refused with the names that differ.
        if (glosslessPolicies.Length > 0 || strayGlosses.Length > 0)
            throw new InvalidOperationException(
                $"The {MorganaPrimerPromptId} prompt no longer matches morgana.json's policies — "
                + $"missing a line for: {string.Join(", ", glosslessPolicies)}; carrying a line for policies that no longer exist: {string.Join(", ", strayGlosses)}.");

        // The policies the author never acts on are separated from the two whose use the interview decides.
        string[] silentPolicies = [.. policies.Select(policy => policy.Name).Where(name => !ExpressivenessPolicies.Contains(name))];
        string[] expressivePolicies = [.. policies.Select(policy => policy.Name).Where(name => ExpressivenessPolicies.Contains(name))];

        // The policies block of the primer, written in the two groups below.
        StringBuilder sb = new StringBuilder();

        // Policies that bind with no choice left to the author.
        if (silentPolicies.Length > 0)
        {
            sb.AppendLine(ResolvePromptInjection(AlembicPromptId, "BindingPoliciesSilent"));
            AppendGlossed(sb, silentPolicies, glosses);
        }

        // Policies whose moments the author chooses for the domain.
        if (expressivePolicies.Length > 0)
        {
            sb.AppendLine(ResolvePromptInjection(AlembicPromptId, "BindingPoliciesExpressive"));
            AppendGlossed(sb, expressivePolicies, glosses);
        }

        // The policies block of the primer.
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Lists policies one per line, each carrying what it settles.
    /// </summary>
    private static void AppendGlossed(StringBuilder sb, string[] names, Dictionary<string, string> glosses)
    {
        foreach (string name in names)
            sb.Append("- ").Append(name).Append(" — ").AppendLine(glosses[name]);

        // A blank line separates the list from the text that follows it in the primer.
        sb.AppendLine();
    }

    /// <summary>
    /// Reads what each framework policy settles, as the primer states it for an author, by the policy's name in <c>morgana.json</c>.
    /// </summary>
    private Dictionary<string, string> Glosses() =>
        Resolve(MorganaPrimerPromptId)
            .GetAdditionalPropertyOrDefault<List<Records.GlobalPolicy>>(Constants.PromptProperties.GlobalPolicies, [])
            .ToDictionary(policy => policy.Name, policy => policy.Description);

    /// <summary>
    /// Appends one section of the composed prompt: the shared prose then the job of the step then the pass's own, under one label.
    /// </summary>
    private static void AppendSection(StringBuilder sb, string label, params string?[] parts)
    {
        // The rows present are joined under the one label, so the model sees no seam between them.
        string section = Records.Prompt.Labeled(label,
            string.Join("\n\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim())));
        // A section none of the rows writes is left out of the prompt.
        if (section.Length == 0)
            return;

        // The section follows the previous one after a blank line.
        sb.AppendLine(section);
        sb.AppendLine();
    }

    /// <summary>
    /// Reads <c>alembic.json</c> out of this assembly.
    /// </summary>
    private static Records.Prompt[] LoadAlembicPrompts()
    {
        // alembic.json travels inside Alembic's own assembly.
        Assembly assembly = Assembly.GetExecutingAssembly();

        // The resource is found by its file-name suffix, since the manifest name carries the assembly's namespace.
        string resourceName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith(".alembic.json", StringComparison.OrdinalIgnoreCase));

        // A missing resource is a packaging defect that stops Alembic at the first prompt.
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("Resource alembic.json is not embedded in Alembic.");

        // The file holds the same prompt shape as an agents.json or a morgana.json.
        Records.PromptCollection? collection = JsonSerializer.Deserialize<Records.PromptCollection>(
            stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Without its prose Alembic has nothing to say: an empty file stops here instead of conducting an interview on an empty prompt.
        return collection?.Prompts is { Length: > 0 } prompts
            ? prompts
            : throw new InvalidOperationException("alembic.json declares no prompts.");
    }
}
