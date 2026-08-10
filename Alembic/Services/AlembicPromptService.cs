using System.Reflection;
using System.Text;
using System.Text.Json;
using Alembic.Interfaces;
using Morgana.AI;
using Morgana.AI.Interfaces;

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

    // Fences, in the framework's own idiom and for the framework's own reason: two layers carry
    // overlapping section labels, and without a boundary the composed prompt shows [PERSONALITY]
    // twice with nothing saying which is which.
    //
    // Two layers, not three, and that is still true of what the model reads: Morgana, then Alembic.
    // What changed is where Alembic's half is stored. Four passes that differ only in which tools
    // they hold were carrying four copies of the same conducting rules, the same voice and the same
    // output format — 22 000 characters of which half was duplication, and duplication in a prompt
    // is not merely long: it is four places to edit a rule and three chances to leave one behind.
    //
    // So the identical part lives once, in the "Alembic" prompt, and a pass carries only what is
    // its own — what it settles, what it must leave alone, how it goes about that. The two are
    // merged section by section here, under one set of labels, because the composed prompt must
    // still be the four sections an agent prompt always is: the model sees no seam.
    private const string MorganaLayerHeader =
        "======== MORGANA — WHOSE VESSEL YOU ARE ========\n" +
        "You are an instrument of Morgana. This is her voice, and it is yours: it is not a description of someone else, and it is not overridable.";
    private const string MorganaLayerFooter = "======== END OF MORGANA ========";
    private const string AlembicLayerHeader =
        "======== ALEMBIC ========\n" +
        "What follows specialises Morgana's voice for the step of the interview you are conducting right now. It adds that and NOTHING ELSE, and it never contradicts the layer above.";

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
    public Records.Prompt Resolve(string promptId) =>
        alembicPrompts.Value.FirstOrDefault(p => string.Equals(p.ID, promptId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Prompt '{promptId}' is not declared in alembic.json.");

    /// <inheritdoc />
    public async Task<string> ComposeAsync(string interviewerId)
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
        
        // Alembic
        Records.Prompt interviewer = Resolve(interviewerId);
        Records.Prompt alembic = Resolve(AlembicPromptId);
        sb.AppendLine(AlembicLayerHeader);
        sb.AppendLine();
        AppendSection(sb, alembic.Target, interviewer.Target);
        AppendSection(sb, alembic.Personality, interviewer.Personality);
        AppendSection(sb, alembic.Instructions, interviewer.Instructions);
        AppendSection(sb, alembic.Formatting, interviewer.Formatting);

        return sb.ToString();
    }

    /// <summary>
    /// Names the policies already binding on every agent Alembic writes.
    /// </summary>
    /// <remarks>
    /// Names only. What Alembic has to know is which subjects are settled above the agent, so that
    /// it writes none of them again; how they are settled is the agent's business at runtime and
    /// not the author's, and the bodies run to some 14 000 characters of turn mechanics Alembic has
    /// no turn to apply them to.
    /// </remarks>
    private static string BindingPolicies(Records.Prompt morgana)
    {
        List<Records.GlobalPolicy> policies =
            morgana.GetAdditionalPropertyOrDefault<List<Records.GlobalPolicy>>("GlobalPolicies", []);

        if (policies.Count == 0)
            return string.Empty;

        return "ALREADY BINDING on every agent written here, stated above it and with more authority: "
               + string.Join(", ", policies.Select(p => p.Name))
               + ". Never write a rule about any of these subjects into an agent's own prose.";
    }

    /// <summary>
    /// Appends one section of the composed prompt: what Alembic always says under this label, then
    /// what this interviewer adds under it.
    /// </summary>
    /// <param name="sb">The prompt being built.</param>
    /// <param name="alembics">What Alembic says in every interview. Carries the section label.</param>
    /// <param name="interviewers">What this interviewer adds. Unlabelled, because the label is above it.</param>
    private static void AppendSection(StringBuilder sb, string? alembics, string? interviewers)
    {
        if (string.IsNullOrWhiteSpace(alembics) && string.IsNullOrWhiteSpace(interviewers))
            return;

        foreach (string? part in new[] { alembics, interviewers })
            if (!string.IsNullOrWhiteSpace(part))
            {
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
