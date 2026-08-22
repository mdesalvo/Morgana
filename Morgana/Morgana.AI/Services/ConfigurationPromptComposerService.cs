using System.Text;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IPromptComposerService"/>: assembles what the model reads from the framework
/// layer and the injection templates carried by <c>morgana.json</c>, resolved once through
/// <see cref="IPromptResolverService"/> and cached for the process lifetime.
/// </summary>
/// <remarks>
/// The framework prompt is resolved lazily rather than in the constructor: the composer is a
/// singleton created during DI graph construction, and resolution is asynchronous.
/// </remarks>
public class ConfigurationPromptComposerService : IPromptComposerService
{
    /// <summary>
    /// Placeholder in the ToolDescriptionContextGuidance injection template, resolved to the
    /// comma-separated names of the tool's own context-scoped parameters.
    /// </summary>
    private const string ContextParametersPlaceholder = "((context_parameters))";

    /// <summary>
    /// Placeholder in the HeldContextDeclaration injection template, resolved per turn to the
    /// comma-separated names of the variables the session currently holds.
    /// </summary>
    private const string HeldVariablesPlaceholder = "((held_variables))";

    /// <summary>
    /// Value of <see cref="Records.ToolParameter.Scope"/> marking a parameter resolved from the
    /// session's context variables rather than asked of the user.
    /// </summary>
    private const string ContextScope = "context";

    // Structural boundary markers for the composed prompt. These are glue between the framework
    // and domain layers, not domain-tunable prose, so — unlike everything they surround — they are
    // fixed in code and morgana.json carries no override point for them. An implementation
    // replacing this service replaces them wholesale, which is the intended unit of substitution:
    // the fences and the layering they express are one design, not a set of independent knobs.
    private const string GlobalPoliciesHeader = "=== CRITICAL RULES — binding, without exception ===";
    private const string GlobalPoliciesFooter = "=== END OF CRITICAL RULES ===";
    private const string FrameworkLayerHeader =
        "======== MORGANA FRAMEWORK — THE LAW OF EVERY TURN ========\n" +
        "Everything until the end of this block is binding on you and on every other agent of Morgana. It is not advice and it is not overridable.";
    private const string FrameworkLayerFooter = "======== END OF MORGANA FRAMEWORK ========";
    private const string DomainLayerHeader =
        "======== DOMAIN AGENT — SUBORDINATE TO THE FRAMEWORK ABOVE ========\n" +
        "What follows specialises the framework for a single domain: what you are for, how you work, how you speak, how you present. It adds domain knowledge and NOTHING ELSE. It NEVER contradicts the framework above, on any point — where the two appear to differ, the framework governs and you follow it.";

    /// <summary>
    /// The framework layer and its global policies, resolved once on first use.
    /// </summary>
    private readonly Lazy<Task<FrameworkLayer>> frameworkLayer;

    /// <summary>
    /// Initializes the composer over the prompt source it composes from.
    /// </summary>
    /// <param name="promptResolverService">Resolves the <c>Morgana</c> framework prompt.</param>
    public ConfigurationPromptComposerService(IPromptResolverService promptResolverService)
    {
        frameworkLayer = new Lazy<Task<FrameworkLayer>>(async () =>
        {
            Records.Prompt prompt = await promptResolverService.ResolveAsync("Morgana");
            return new FrameworkLayer(
                prompt,
                prompt.GetAdditionalProperty<List<Records.GlobalPolicy>>("GlobalPolicies"));
        });
    }

    /// <inheritdoc />
    public async Task<string> ComposeAgentInstructionsAsync(Records.Prompt domainPrompt)
    {
        FrameworkLayer framework = await frameworkLayer.Value;

        StringBuilder sb = new StringBuilder();

        // The two layers are fenced. Both carry the same four section labels ([TARGET],
        // [PERSONALITY], [INSTRUCTIONS], [FORMATTING]), so without a delimiter the composed prompt
        // shows each of them twice with nothing marking which is which — and the framework's claim
        // to precedence, made in its own Target, names a boundary the model cannot locate. The
        // domain fence carries the subordination rule itself rather than only separating: it is read
        // at the exact point where the layer it governs begins.

        // Morgana layers
        sb.AppendLine(FrameworkLayerHeader);
        sb.AppendLine();
        sb.AppendLine(framework.Prompt.Target);
        sb.AppendLine();
        sb.AppendLine(framework.Prompt.Personality);
        sb.AppendLine();
        sb.AppendLine(FormatGlobalPolicies(framework.Policies));
        sb.AppendLine();
        sb.AppendLine(framework.Prompt.Instructions);
        sb.AppendLine();
        sb.AppendLine(framework.Prompt.Formatting);
        sb.AppendLine();
        sb.AppendLine(FrameworkLayerFooter);
        sb.AppendLine();

        // Agent layers
        sb.AppendLine(DomainLayerHeader);
        sb.AppendLine();
        sb.AppendLine(domainPrompt.Target);
        sb.AppendLine();
        sb.AppendLine(domainPrompt.Personality);
        sb.AppendLine();
        sb.AppendLine(domainPrompt.Instructions);
        sb.AppendLine();
        sb.AppendLine(domainPrompt.Formatting);
        sb.AppendLine();

        return sb.ToString();
    }

    /// <inheritdoc />
    public async Task<string> ComposeToolDescriptionAsync(Records.ToolDefinition toolDefinition)
    {
        FrameworkLayer framework = await frameworkLayer.Value;

        string descriptionGuidance = Records.GlobalPolicy.ResolveTemplate(
            framework.Policies, Records.GlobalPolicy.Templates.ToolDescriptionContext);

        // Extract context-scoped parameter names; if present and guidance template exists, splice names into guidance text
        string[] contextParameters = [.. toolDefinition.Parameters
            .Where(p => string.Equals(p.Scope?.Trim(), ContextScope, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)];

        return contextParameters.Length > 0 && descriptionGuidance.Length > 0
            ? $"{toolDefinition.Description}\n\n{descriptionGuidance.Replace(ContextParametersPlaceholder, string.Join(", ", contextParameters))}"
            : toolDefinition.Description;
    }

    /// <inheritdoc />
    public async Task<string?> ComposeHeldContextDeclarationAsync(IReadOnlyCollection<string> heldVariables)
    {
        if (heldVariables.Count == 0)
            return null;

        FrameworkLayer framework = await frameworkLayer.Value;

        string declaration = Records.GlobalPolicy.ResolveTemplate(framework.Policies, Records.GlobalPolicy.Templates.HeldContextDeclaration);

        // ResolveTemplate returns "" for a template the prompt layer does not declare, which every
        // splice site reads as "inject nothing".
        return declaration.Length == 0
            ? null
            : declaration.Replace(HeldVariablesPlaceholder, string.Join(", ", heldVariables));
    }

    /// <summary>
    /// Renders the global policies into the fenced block that opens the framework layer.
    /// </summary>
    private static string FormatGlobalPolicies(List<Records.GlobalPolicy> policies)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine(GlobalPoliciesHeader);

        // Injection templates are excluded: they are not policies but fragments spliced into the
        // description of the tool (or parameter) they govern, at the point where the model decides.
        //
        // Ordered by Type, then ascending Priority within each type. The LLM reads the system
        // prompt top-to-bottom and the order is load-bearing for compliance, so a policy's
        // Priority states where it must be read, not merely how it was filed.
        foreach (Records.GlobalPolicy policy in policies.Where(p => !p.IsInjectionTemplate)
                                                        .OrderBy(p => p.Type)
                                                        .ThenBy(p => p.Priority))
        {
            sb.AppendLine($"{policy.Name}: {policy.Description}");
        }

        sb.AppendLine(GlobalPoliciesFooter);

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The framework prompt and its unpacked global policies, resolved once and reused.
    /// </summary>
    private sealed record FrameworkLayer(Records.Prompt Prompt, List<Records.GlobalPolicy> Policies);
}