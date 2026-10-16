using System.Globalization;
using System.Text;
using System.Text.Json;
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
        "=== RULES OF EVERY TURN ===";
    private const string GlobalPoliciesFooter =
        "=== END OF RULES ===";
    private const string FrameworkLayerHeader =
        "======== MORGANA FRAMEWORK ========\n" +
        "Everything until the end of this block binds you and every other agent of Morgana.";
    private const string FrameworkLayerFooter =
        "======== END OF MORGANA FRAMEWORK ========";
    private const string DomainLayerHeader =
        "======== DOMAIN AGENT ========\n" +
        "What follows specialises the framework above for a single domain: what you are for, how you work, how you speak, how you present. It adds domain knowledge and nothing else.";
    private const string DomainLayerFooter =
        "======== END OF DOMAIN AGENT ========";

    /// <summary>
    /// Names the asking party when the request declares no intent. Only an agent of a Morgana declares
    /// one and the A2A door admits only declared systems, so an unnamed caller is precisely that.
    /// Saying so is a fact. A phrase where every other caller is a bare word, which is why every caller
    /// is quoted before it reaches the prose.
    /// </summary>
    private const string UnnamedCaller = "an external system";

    /// <summary>
    /// Encloses whoever is asking, so the prose reads it as a name wherever it lands.
    /// </summary>
    /// <remarks>
    /// A named caller is one word and needs nothing; an unnamed one resolves to a phrase that would
    /// otherwise dissolve into the sentence around it. Quoting every caller keeps one prose written
    /// for one shape rather than two that have to read well apart.
    /// </remarks>
    private const string CallerQuotation = "«{0}»";

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
        // Deferred to the first composition so that building the composer never reads the prompt source.
        frameworkLayer = new Lazy<Task<FrameworkLayer>>(async () =>
        {
            // The Morgana prompt carries the four framework sections and every list of policies and templates.
            Records.Prompt prompt = await promptResolverService.ResolveAsync(Constants.Morgana);

            // Tool injections are optional: a framework layer declaring none leaves the model's tools undecorated.
            return new FrameworkLayer(
                prompt,
                prompt.GetAdditionalProperty<List<Records.GlobalPolicy>>(Constants.PromptProperties.GlobalPolicies),
                prompt.GetAdditionalProperty<List<Records.Injection>>(Constants.PromptProperties.PromptInjections),
                prompt.GetAdditionalPropertyOrDefault<List<Records.Injection>>(Constants.PromptProperties.ToolInjections, []));
        });
    }

    /// <inheritdoc />
    public async Task<string> ComposeAgentInstructionsAsync(Records.Prompt domainPrompt, bool peerCapable = false)
    {
        // Both halves are used below: the prompt's four sections become the framework block, its
        // policies the fenced list of rules inside it.
        FrameworkLayer framework = await frameworkLayer.Value;

        // The agent's instructions are accumulated here, one fenced section after the other.
        StringBuilder sb = new StringBuilder();

        // The framework layer opens the prompt and is fenced, so the model reads it as the law over the domain below.
        sb.AppendLine(FrameworkLayerHeader);
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Target, framework.Prompt.Target));
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Personality, framework.Prompt.Personality));
        sb.AppendLine();
        sb.AppendLine(FormatGlobalPolicies(framework.Policies, peerCapable));
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Instructions, framework.Prompt.Instructions));
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Formatting, framework.Prompt.Formatting));
        sb.AppendLine();
        sb.AppendLine(FrameworkLayerFooter);
        sb.AppendLine();

        // The domain layer follows, fenced as subordinate: it carries no policies of its own.
        sb.AppendLine(DomainLayerHeader);
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Target, domainPrompt.Target));
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Personality, domainPrompt.Personality));
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Instructions, domainPrompt.Instructions));
        sb.AppendLine();
        sb.AppendLine(Records.Prompt.Labeled(Constants.SectionLabels.Formatting, domainPrompt.Formatting));
        sb.AppendLine();
        sb.AppendLine(DomainLayerFooter);
        sb.AppendLine();

        // The agent's whole instructions, composed once at its creation.
        return sb.ToString();
    }

    /// <inheritdoc />
    public async Task<string> ComposeToolDescriptionAsync(Records.ToolDefinition toolDefinition)
    {
        // The return contract follows the authored description, so the guidance below stays last.
        string description = await ComposeReturnContractAsync(toolDefinition.Description, toolDefinition.ReturnSchema);

        // A tool that needs no approval ends with its contract.
        if (!toolDefinition.RequiresExecutionApproval)
            return description;

        // Without the guidance the model asks for a go-ahead before calling and the user is asked twice.
        // Said once here, never by each tool's author.
        FrameworkLayer framework = await frameworkLayer.Value;
        string guidance = Records.Injection.ResolveTemplate(framework.ToolInjections, Constants.ToolInjections.ExecutionApprovalGuidance);
        return guidance.Length == 0 ? description : $"{description}\n\n{guidance}";
    }

    /// <inheritdoc />
    public async Task<string> ComposeReturnContractAsync(string description, JsonElement? returnSchema)
    {
        // A tool returning no record has no contract. A schema that is not an object describes nothing.
        if (returnSchema is not { ValueKind: JsonValueKind.Object } schema)
            return description;

        // A deployment declaring no entry offers the model no return contract.
        FrameworkLayer framework = await frameworkLayer.Value;
        string contract = Records.Injection.ResolveTemplate(framework.ToolInjections, Constants.ToolInjections.ReturnContract);
        if (contract.Length == 0)
            return description;

        // The schema is spliced compact, since every space of it is paid for on each turn.
        return $"{description}\n\n{contract.Replace(Constants.Placeholders.ReturnSchema, schema.GetRawText(), StringComparison.Ordinal)}";
    }

    /// <summary>
    /// Describes a colleague's function by the colleague's own words and nothing of the framework's: how to consult one
    /// is carried by the policy that every peer-capable agent reads and the card's skills are absent on purpose, since
    /// an inventory of functions is what a caller audits to rule a question out.
    /// </summary>
    public Task<string> ComposePeerDescriptionAsync(A2A.AgentCard peerCard)
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
        string declaration = Records.Injection.ResolveTemplate(framework.PromptInjections, Constants.PromptInjections.ColleaguesDeclaration);
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
    public async Task<string?> ComposeLandingDeclarationAsync(IReadOnlyList<Records.OfferedAction> menu)
    {
        // The text that tells a landing agent its menu: the framework's own wording, resolved by name.
        FrameworkLayer framework = await frameworkLayer.Value;
        string declaration = Records.Injection.ResolveTemplate(framework.PromptInjections, Constants.PromptInjections.LandingDeclaration);

        // A deployment declaring no text has its landing turn run on the agent's instructions alone.
        if (declaration.Length == 0)
            return null;

        // The labels in menu order are what the introduction can refer to; an agent that offers nothing says so,
        // so the introduction does not promise buttons that will not come.
        string actions = menu.Count == 0 ? "none" : string.Join(", ", menu.Select(action => action.Label));

        // The menu is the agent's own and was fixed when the agent was built: the model gives it a voice and never chooses it.
        return declaration.Replace(Constants.Placeholders.Actions, actions);
    }

    /// <inheritdoc />
    public async Task<Records.ConsultationRequest> ComposeConsultationRequestAsync(string? callerIntent, string question)
    {
        // All templates are the framework's: the question is the only text here that somebody else wrote.
        FrameworkLayer framework = await frameworkLayer.Value;

        // How to answer a colleague. It travels in the run's own instructions instead of the answering
        // agent's prompt: that prompt is composed once, while whether a turn serves a colleague
        // changes turn by turn.
        string declaration = Records.Injection.ResolveTemplate(
            framework.PromptInjections, Constants.PromptInjections.PeerConsultationDeclaration);

        // What the question may not claim and what asking cannot obtain, kept apart from the note on
        // how to answer: two rules of one length dilute each other, while only this one has to hold
        // against text somebody else wrote.
        string guardrail = Records.Injection.ResolveTemplate(
            framework.PromptInjections, Constants.PromptInjections.PeerConsultationGuardrail);

        // The fence the question is read inside, kept out of the rules: the rules sit in the run's
        // instructions while the fenced question is the turn's message.
        string fence = Records.Injection.ResolveTemplate(
            framework.PromptInjections, Constants.PromptInjections.PeerConsultationQuestion);

        // The caller names itself or it does not and the wording of what an unnamed one is called
        // belongs here, with the rest of the prose this layer authors, rather than at the call site
        // that merely failed to find a name. Resolved while the colleague's own words are still out:
        // a question is answered, never read for placeholders of this layer to fill.
        string caller = string.Format(
            CultureInfo.InvariantCulture,
            CallerQuotation, string.IsNullOrWhiteSpace(callerIntent) ? UnnamedCaller : callerIntent);

        // A deployment declaring neither text has the run carry no instructions of this kind.
        string instructions = string.Join("\n", new[] { declaration, guardrail }.Where(text => text.Length > 0))
            .Replace(Constants.Placeholders.ConsultationCaller, caller);

        // Nothing encloses the question where a deployment declares no fence, so it is handed over as
        // it arrived rather than under a marker announcing a boundary that is not there. The caller is
        // filled before the question goes in, so the colleague's words are never read for placeholders.
        string fencedQuestion = fence.Length == 0
            ? question
            : fence.Replace(Constants.Placeholders.ConsultationCaller, caller).Replace(Constants.Placeholders.ConsultationQuestion, question);

        // The instructions go to the run and the question to the turn's message: what somebody else wrote never shares a message with the rules that bind it.
        return new Records.ConsultationRequest(instructions.Length == 0 ? null : instructions, fencedQuestion);
    }

    /// <inheritdoc />
    public async Task<string?> ComposeTurnClosureRequestAsync()
    {
        // The request that follows a turn which the model wrote without closing it.
        FrameworkLayer framework = await frameworkLayer.Value;
        string request = Records.Injection.ResolveTemplate(framework.PromptInjections, Constants.PromptInjections.TurnClosureRequest);
        // A deployment declaring no request has the turn closed without one.
        return request.Length == 0 ? null : request;
    }

    /// <inheritdoc />
    public async Task<string> ComposeTurnClosureNotAcceptedAsync(string reason)
    {
        // Spliced at the one moment it is true, in place of the refused call's result, rather than
        // carried by every prompt for an event most turns never meet.
        FrameworkLayer framework = await frameworkLayer.Value;
        string template = Records.Injection.ResolveTemplate(framework.ToolInjections, Constants.ToolInjections.TurnClosureNotAccepted);

        // A deployment declaring no text has the model told the bare reason.
        return template.Length == 0 ? reason : template.Replace(Constants.Placeholders.TurnClosureNotAcceptedReason, reason);
    }

    /// <inheritdoc />
    public async Task<string?> ComposeEarlierToolResultAsync(string result)
    {
        // The result is spliced last: it is the tool's text and is never read for placeholders of this layer.
        FrameworkLayer framework = await frameworkLayer.Value;
        string template = Records.Injection.ResolveTemplate(framework.ToolInjections, Constants.ToolInjections.EarlierToolResult);

        // A deployment declaring no text leaves the earlier result unmarked.
        return template.Length == 0 ? null : template.Replace(Constants.Placeholders.EarlierToolResultContent, result);
    }

    /// <inheritdoc />
    public async Task<string?> ComposeWorkflowResultAsync(string workflow, string? step, string result)
    {
        // A result with a step is the workflow reaching it: one with none is the workflow ending.
        FrameworkLayer framework = await frameworkLayer.Value;
        string template = Records.Injection.ResolveTemplate(
            framework.ToolInjections,
            step is null ? Constants.ToolInjections.WorkflowEnded : Constants.ToolInjections.WorkflowStepReached);

        // The result is spliced last: it is the tool's text and is never read for placeholders of this layer.
        return template.Length == 0
            ? null
            : template
                .Replace(Constants.Placeholders.Workflow, workflow, StringComparison.Ordinal)
                .Replace(Constants.Placeholders.Step, step ?? string.Empty, StringComparison.Ordinal)
                .Replace(Constants.Placeholders.EarlierToolResultContent, result, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    /// <summary>Resolves one ToolInjections text by name, filling its placeholders from the values.</summary>
    public async Task<string> ComposeToolInjectionAsync(string name, IReadOnlyDictionary<string, string>? values = null)
        => Records.Injection.Resolve((await frameworkLayer.Value).ToolInjections, name, values);

    /// <summary>
    /// Renders the global policies into the fenced block that opens the framework layer.
    /// </summary>
    /// <param name="policies">The framework prompt's own policy list.</param>
    /// <param name="peerCapable">Admits the peer-consultation policy, skipped for every other agent.</param>
    private static string FormatGlobalPolicies(List<Records.GlobalPolicy> policies, bool peerCapable)
    {
        // The rendered policies, written in the order the model reads them.
        StringBuilder sb = new StringBuilder();

        // The header fences the policies off from the rest of the prompt.
        sb.AppendLine(GlobalPoliciesHeader);

        // One line per policy: its name and what it requires, in the order the model must read them.
        foreach (Records.GlobalPolicy policy in policies
                     // The one rule whose subject may not exist. An agent outside the A2A topology is
                     // never asked by a colleague, so it would carry this on every turn of its life.
                     .Where(p => peerCapable || !string.Equals(
                         p.Name, Constants.Policies.PeerConsultation,
                         StringComparison.OrdinalIgnoreCase))

                     // The model reads top to bottom, so a policy's Priority states where it must be
                     // read rather than how it was filed.
                     .OrderBy(p => p.Priority))
        {
            // Each policy reads as its name followed by what it requires.
            sb.AppendLine(CultureInfo.InvariantCulture, $"{policy.Name}: {policy.Description}");
        }

        // The footer closes that fence.
        sb.AppendLine(GlobalPoliciesFooter);

        // The trailing newline is dropped so that the caller decides the spacing around the block.
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The framework prompt and the lists that it declares beside its four sections, resolved once and
    /// reused: the rules rendered into every agent's prompt, the templates spliced into the instructions or the messages and the
    /// texts that the model reads as part of a tool.
    /// </summary>
    private sealed record FrameworkLayer(
        Records.Prompt Prompt,
        List<Records.GlobalPolicy> Policies,
        List<Records.Injection> PromptInjections,
        List<Records.Injection> ToolInjections);
}