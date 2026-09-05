using System.Text;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IPromptComposerService"/>: assembles what the model reads from the framework
/// layer and the injection templates carried by <c>morgana.json</c>, resolved once through
/// <see cref="IPromptResolverService"/> and cached for the process lifetime.
/// </summary>
public class ConfigurationPromptComposerService : IPromptComposerService
{
    // Structural boundary markers for the composed prompt. These are glue between the framework
    // and domain layers, not domain-tunable prose, so — unlike everything they surround — they are
    // fixed in code and morgana.json carries no override point for them. An implementation
    // replacing this service replaces them wholesale, which is the intended unit of substitution:
    // the fences and the layering they express are one design, not a set of independent knobs.
    private const string GlobalPoliciesHeader =
        "=== CRITICAL RULES — binding, without exception ===";
    private const string GlobalPoliciesFooter =
        "=== END OF CRITICAL RULES ===";
    private const string FrameworkLayerHeader =
        "======== MORGANA FRAMEWORK — THE LAW OF EVERY TURN ========\n" +
        "Everything until the end of this block is binding on you and on every other agent of Morgana. It is not advice and it is not overridable.";
    private const string FrameworkLayerFooter =
        "======== END OF MORGANA FRAMEWORK ========";
    private const string DomainLayerHeader =
        "======== DOMAIN AGENT — SUBORDINATE TO THE FRAMEWORK ABOVE ========\n" +
        "What follows specialises the framework for a single domain: what you are for, how you work, how you speak, how you present. It adds domain knowledge and NOTHING ELSE. It NEVER contradicts the framework above, on any point — where the two appear to differ, the framework governs and you follow it.";
    private const string DomainLayerFooter =
        "======== END OF DOMAIN AGENT ========";

    /// <summary>
    /// Names the asking party when the request declares no intent. Only an agent of a Morgana declares
    /// one and the A2A door admits only declared systems, so an unnamed caller is precisely that — and
    /// saying so is a fact. It has to read as a name in every sentence of that prose, possessive included.
    /// </summary>
    private const string UnnamedCaller = "an external system";

    /// <summary>
    /// The framework prompt with its policies already unpacked. morgana.json is read once per process:
    /// the first composition of any kind pays for it, every later one observes the same layer.
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
            Records.Prompt prompt = await promptResolverService.ResolveAsync(Constants.Morgana);
            return new FrameworkLayer(
                prompt,
                prompt.GetAdditionalProperty<List<Records.GlobalPolicy>>("GlobalPolicies"));
        });
    }

    /// <inheritdoc />
    public async Task<string> ComposeAgentInstructionsAsync(Records.Prompt domainPrompt, bool peerCapable = false)
    {
        // Both halves are used below: the prompt's four sections become the framework block, its
        // policies the fenced list of rules inside it.
        FrameworkLayer framework = await frameworkLayer.Value;

        StringBuilder sb = new StringBuilder();

        // Framework
        sb.AppendLine(FrameworkLayerHeader);
        sb.AppendLine();
        sb.AppendLine(framework.Prompt.Target);
        sb.AppendLine();
        sb.AppendLine(framework.Prompt.Personality);
        sb.AppendLine();
        sb.AppendLine(FormatGlobalPolicies(framework.Policies, peerCapable));
        sb.AppendLine();
        sb.AppendLine(framework.Prompt.Instructions);
        sb.AppendLine();
        sb.AppendLine(framework.Prompt.Formatting);
        sb.AppendLine();
        sb.AppendLine(FrameworkLayerFooter);
        sb.AppendLine();

        // Domain
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
        sb.AppendLine(DomainLayerFooter);
        sb.AppendLine();

        return sb.ToString();
    }

    /// <inheritdoc />
    public async Task<string> ComposeToolDescriptionAsync(Records.ToolDefinition toolDefinition)
    {
        // The lookup-before-asking rule, authored once in morgana.json instead of restated by every
        // tool author. Empty when a deployment declares no such template.
        FrameworkLayer framework = await frameworkLayer.Value;
        string descriptionGuidance = Records.GlobalPolicy.ResolveTemplate(
            framework.Policies, Constants.Injections.ToolDescriptionContextGuidance);

        // The inputs this tool resolves from the session rather than from the user. They are what the
        // guidance names, so a tool with none has nothing to be guided about.
        string[] contextParameters = [.. toolDefinition.Parameters
            .Where(p => string.Equals(p.Scope?.Trim(), Constants.Scopes.Context, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Name)];

        // Guidance is joined by a blank line rather than by inserted punctuation: an authored
        // description is a finished sentence that closes itself. A tool with no context-scoped input
        // reaches the model exactly as its author wrote it.
        return contextParameters.Length > 0 && descriptionGuidance.Length > 0
            ? $"{toolDefinition.Description}\n\n{descriptionGuidance.Replace(Constants.Placeholders.ContextParameters, string.Join(", ", contextParameters))}"
            : toolDefinition.Description;
    }

    /// <inheritdoc />
    public Task<string> ComposePeerDescriptionAsync(A2A.AgentCard peerCard)
        // The colleague's own words and nothing of the framework's: how to consult one is already
        // carried by the policy every peer-capable agent reads and repeating it per colleague would
        // pay for it once per function. The card's skills are deliberately absent too — an inventory
        // of the colleague's functions is what a caller audits to rule a question out and what falls
        // to that desk is the colleague's own to state.
        => Task.FromResult(peerCard.Description ?? "");

    /// <inheritdoc />
    public async Task<string?> ComposeColleaguesDeclarationAsync(IReadOnlyDictionary<string, string> colleagues)
    {
        // An agent declaring no [ConsultsAgent] never reads a word about colleagues.
        if (colleagues.Count == 0)
            return null;

        // The rung that closes a peer-capable agent's instructions. Undeclared by a deployment wanting
        // no such rung, which leaves those instructions exactly as the two layers composed them.
        FrameworkLayer framework = await frameworkLayer.Value;
        string declaration = Records.GlobalPolicy.ResolveTemplate(framework.Policies, Constants.Injections.ColleaguesDeclaration);
        if (declaration.Length == 0)
            return null;

        // One line per colleague, the callable name in front of the territory it covers: what the
        // model has to join is "this request is theirs" with "this is the function that reaches them".
        string lines = string.Join("\n", colleagues.Select(kvp => $"- {kvp.Key}: {kvp.Value}"));

        // Closes the composed instructions once, for the agent's whole life: who its colleagues are is
        // settled at creation, so this rides in the cached prefix instead of being paid per turn.
        return declaration.Replace(Constants.Placeholders.Colleagues, lines);
    }

    /// <inheritdoc />
    public async Task<string> ComposeConsultationRequestAsync(string? callerIntent)
    {
        // The whole of how to answer a colleague. It is spliced in front of the incoming question
        // instead of into the answering agent's prompt: that prompt is composed once, while whether a
        // turn serves a colleague changes turn by turn.
        FrameworkLayer framework = await frameworkLayer.Value;
        string declaration = Records.GlobalPolicy.ResolveTemplate(
            framework.Policies, Constants.Injections.PeerConsultationDeclaration);

        // The caller names itself or it does not and the wording of what an unnamed one is called
        // belongs here, with the rest of the prose this layer authors, rather than at the call site
        // that merely failed to find a name.
        return declaration.Replace(
            Constants.Placeholders.ConsultationCaller,
            string.IsNullOrWhiteSpace(callerIntent) ? UnnamedCaller : callerIntent);
    }

    /// <inheritdoc />
    public async Task<string?> ComposeHeldContextDeclarationAsync(IReadOnlyDictionary<string, object> heldVariables)
    {
        // A session holding nothing gets no injection at all.
        if (heldVariables.Count == 0)
            return null;

        // The one framework entry that carries a fact rather than a rule. It is also the only rung read
        // before any tool is weighed: a tool description can state the contract, never what is held now.
        FrameworkLayer framework = await frameworkLayer.Value;
        string declaration = Records.GlobalPolicy.ResolveTemplate(framework.Policies, Constants.Injections.HeldContextDeclaration);

        // A deployment declaring no such template gets no per-turn tail.
        if (declaration.Length == 0)
            return null;

        // Values and not merely names: an agent waking on a shared variable it never asked for has
        // nothing left to look up. The names-only variant relied on the model choosing to call
        // GetContextVariable, which proved unreliable.
        string pairs = string.Join(", ", heldVariables.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
        string resolvedDeclaration = declaration.Replace(Constants.Placeholders.HeldVariables, pairs);

        // Marked so the cache split lands above this tail. It changes every turn while the framework
        // and domain layers before it do not. A changing tail must not bust the whole prefix.
        return Constants.Markers.DynamicInstructions + resolvedDeclaration;
    }

    /// <summary>
    /// Renders the global policies into the fenced block that opens the framework layer.
    /// </summary>
    /// <param name="policies">The framework prompt's own policy list, injection templates included.</param>
    /// <param name="peerCapable">Admits the peer-consultation policy, skipped for every other agent.</param>
    private static string FormatGlobalPolicies(List<Records.GlobalPolicy> policies, bool peerCapable)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine(GlobalPoliciesHeader);

        foreach (Records.GlobalPolicy policy in policies
                     // Injection templates share the list, but are not policies: each is spliced where
                     // it has a referent. Rendered here it would instruct against nothing.
                     .Where(p => !p.IsInjectionTemplate)

                     // The one rule whose subject may not exist. An agent outside the A2A topology is
                     // never asked by a colleague, so it would carry this on every turn of its life.
                     .Where(p => peerCapable || !string.Equals(
                         p.Name, Constants.Policies.PeerConsultation,
                         StringComparison.OrdinalIgnoreCase))

                     // The model reads top to bottom, so a policy's Priority states where it must be
                     // read rather than how it was filed.
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