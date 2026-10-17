using System.Reflection;
using System.Text;
using System.Text.Json;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>
/// Composes the judge's instructions from <c>harness.json</c> beneath the framework primer.
/// </summary>
/// <remarks>The file is matched by its name suffix, so the prose survives a rename of the assembly.</remarks>
public sealed class HarnessPromptService
{
    /// <summary>The <c>morgana.json</c> prompt whose policies the primer glosses.</summary>
    private const string MorganaPromptId = "Morgana";

    /// <summary>The prompt that describes the framework to the judge, with one gloss per binding policy.</summary>
    private const string MorganaPrimerPromptId = "MorganaPrimer";

    /// <summary>The prompt holding the judge's identity, its rules and the template of a judged turn.</summary>
    public const string JudgePromptId = "Judge";

    /// <summary>The harness's own prompts, parsed on the first call that needs them.</summary>
    private readonly Lazy<Records.Prompt[]> harnessPrompts = new(LoadHarnessPrompts);

    /// <summary>Morgana's own prompt from the embedded <c>morgana.json</c>, whose policies the judge's primer glosses.</summary>
    private readonly Lazy<Task<Records.Prompt>> morganaPrompt;

    /// <summary>
    /// Initializes the service over the resolver of Morgana's own prompt.
    /// </summary>
    public HarnessPromptService(IPromptResolverService promptResolverService)
    {
        morganaPrompt = new Lazy<Task<Records.Prompt>>(() => promptResolverService.ResolveAsync(MorganaPromptId));
    }

    /// <summary>Writes a placeholder name the way the texts of <c>harness.json</c> carry it, <c>((name))</c>.</summary>
    private static string Placeholder(string name) => $"(({name}))";

    /// <summary>
    /// Returns the prompt declared in <c>harness.json</c> under the given id.
    /// </summary>
    public Records.Prompt Resolve(string promptId) =>
        harnessPrompts.Value.FirstOrDefault(prompt => string.Equals(prompt.ID, promptId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Prompt '{promptId}' is not declared in harness.json.");

    /// <summary>
    /// Returns an injection of a harness prompt with its placeholders filled by the given values.
    /// </summary>
    public string ResolvePromptInjection(string promptId, string injectionName, params (string Placeholder, string Value)[] values)
    {
        // The injections of the prompt that was asked for: a prompt that declares none is an authoring defect and not an empty set.
        Records.Prompt prompt = Resolve(promptId);
        string text = Records.Injection.ResolveTemplate(
            prompt.GetAdditionalProperty<List<Records.Injection>>(Constants.PromptProperties.PromptInjections), injectionName);

        // The injection reaches the model with its placeholders filled by the values of this call.
        return Spliced(text, $"The {promptId} prompt in harness.json declares no '{injectionName}' injection.", values);
    }

    /// <summary>
    /// Fills the placeholders of an injection's text with the values of the call.
    /// </summary>
    /// <param name="text">The injection as authored; empty when the prompt declares none.</param>
    /// <param name="absence">What the exception says when the text is empty.</param>
    /// <param name="values">Placeholder name to the value that it stands for.</param>
    private static string Spliced(string text, string absence, (string Placeholder, string Value)[] values)
    {
        // A text that is absent stops here: a judge handed a request with a sentence missing would work wrong without any sign of it.
        if (text.Length == 0)
            throw new InvalidOperationException(absence);

        // Each placeholder is replaced literally: the values come from the turn and must never be read as patterns.
        foreach ((string placeholder, string value) in values)
            text = text.Replace(Placeholder(placeholder), value, StringComparison.Ordinal);

        // The sentence as the model reads it.
        return text;
    }

    /// <summary>
    /// Composes the judge's instructions: the framework primer then the judge's own target and instructions.
    /// </summary>
    public async Task<string> ComposeJudgeInstructionsAsync()
    {
        // The policies are read live from morgana.json, so the primer names exactly those that bind.
        Records.Prompt morgana = await morganaPrompt.Value;
        Records.Prompt judge = Resolve(JudgePromptId);

        // The first layer is the framework described to the judge: a header, the primer, one line per binding policy and a footer.
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(ResolvePromptInjection(JudgePromptId, "MorganaPrimerHeader"));
        sb.AppendLine();
        sb.AppendLine(Resolve(MorganaPrimerPromptId).Target);
        sb.AppendLine();
        sb.AppendLine(BindingPolicies(morgana));
        sb.AppendLine(ResolvePromptInjection(JudgePromptId, "MorganaPrimerFooter"));
        sb.AppendLine();

        // The second layer is the judge's own identity and rules, under the framework's section labels.
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Target, judge.Target));
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Instructions, judge.Instructions));

        // The whole system prompt of the judge.
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// States the policies already binding on every agent, each under the one line the primer gives it.
    /// </summary>
    /// <remarks>The names are <c>morgana.json</c>'s while each line is the primer's reading of the policy for the judge.</remarks>
    private string BindingPolicies(Records.Prompt morgana)
    {
        // The policies that morgana.json binds on every agent.
        List<Records.GlobalPolicy> policies =
            morgana.GetAdditionalPropertyOrDefault<List<Records.GlobalPolicy>>(Constants.PromptProperties.GlobalPolicies, []);

        // The primer's line for each policy, keyed by the policy's own name.
        Dictionary<string, string> glosses = Resolve(MorganaPrimerPromptId)
            .GetAdditionalPropertyOrDefault<List<Records.GlobalPolicy>>(Constants.PromptProperties.GlobalPolicies, [])
            .ToDictionary(policy => policy.Name, policy => policy.Description);

        // A policy without a line reaches the judge as a name with nothing behind it and a line without a policy teaches a rule that does not bind.
        string[] glosslessPolicies = [.. policies.Select(policy => policy.Name).Where(name => !glosses.ContainsKey(name))];
        string[] strayGlosses = [.. glosses.Keys.Where(name => policies.All(policy => policy.Name != name))];

        // The two sides disagree: the judge is refused with the names that differ.
        if (glosslessPolicies.Length > 0 || strayGlosses.Length > 0)
            throw new InvalidOperationException(
                $"The {MorganaPrimerPromptId} prompt no longer matches morgana.json's policies: "
                + $"missing a line for: {string.Join(", ", glosslessPolicies)}; carrying a line for policies that no longer exist: {string.Join(", ", strayGlosses)}.");

        // The policies block of the primer: the introducing line then one line per policy in morgana.json's order.
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(ResolvePromptInjection(JudgePromptId, "BindingPolicies"));
        foreach (Records.GlobalPolicy policy in policies)
            sb.Append("- ").Append(policy.Name).Append(" — ").AppendLine(glosses[policy.Name]);

        // A blank line separates the list from the text that follows it in the primer.
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Reads <c>harness.json</c> out of this assembly.
    /// </summary>
    private static Records.Prompt[] LoadHarnessPrompts()
    {
        // harness.json travels inside the harness's own assembly.
        Assembly assembly = Assembly.GetExecutingAssembly();

        // The resource is found by its file-name suffix, since the manifest name carries the assembly's namespace.
        string resourceName = assembly.GetManifestResourceNames()
            .First(name => name.EndsWith(".harness.json", StringComparison.OrdinalIgnoreCase));

        // A missing resource is a packaging defect that stops the judge at the first prompt.
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("Resource harness.json is not embedded in PromptHarness.");

        // The file holds the same prompt shape as an agents.json or a morgana.json.
        Records.PromptCollection? collection = JsonSerializer.Deserialize<Records.PromptCollection>(
            stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        // Without its prose the judge has nothing to say: an empty file stops here instead of judging on an empty prompt.
        return collection?.Prompts is { Length: > 0 } prompts
            ? prompts
            : throw new InvalidOperationException("harness.json declares no prompts.");
    }
}
