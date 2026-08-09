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
    /// The shared layer every pass is composed beneath.
    /// </summary>
    private const string DoctrinePromptId = "Doctrine";

    // Fences, in the framework's own idiom and for the framework's own reason: three layers carry
    // overlapping section labels, and without a boundary the composed prompt shows [TARGET] and
    // [PERSONALITY] more than once with nothing saying which is which.
    private const string MorganaLayerHeader =
        "======== MORGANA — WHOSE VESSEL YOU ARE ========\n" +
        "You are an instrument of Morgana. This is her voice, and it is yours: it is not a description of someone else, and it is not overridable.";
    private const string MorganaLayerFooter = "======== END OF MORGANA ========";
    private const string AlembicLayerHeader =
        "======== ALEMBIC — WHAT YOU DO WITH THAT VOICE ========\n" +
        "What follows specialises Morgana's voice for one task: distilling a new agent of hers out of an interview. It adds that task and NOTHING ELSE, and it never contradicts the layer above.";
    private const string PassLayerHeader =
        "======== THIS PASS ========\n" +
        "What follows is the single pass you are conducting right now. Everything above holds throughout; this says only what this pass settles and how it answers.";

    /// <summary>
    /// Alembic's own prompts, parsed once on first use.
    /// </summary>
    private readonly Lazy<Records.Prompt[]> prompts = new(LoadPrompts);

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
        prompts.Value.FirstOrDefault(p => string.Equals(p.ID, promptId, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"Prompt '{promptId}' is not declared in alembic.json.");

    /// <inheritdoc />
    public async Task<string> ComposeAsync(string passId)
    {
        Records.Prompt morgana = await morganaPrompt.Value;
        Records.Prompt doctrine = Resolve(DoctrinePromptId);
        Records.Prompt pass = Resolve(passId);

        StringBuilder sb = new StringBuilder();

        // Morgana's Personality and nothing else of hers.
        //
        // Her Personality IS her identity — a good witch whose magic reaches the user as a result
        // and never as a procedure they are made to watch — and Alembic is an instrument of hers,
        // so it inherits it rather than inventing a character next to it.
        //
        // Her Target is deliberately left out: it is a preamble about a two-layer agent prompt and
        // "the system tools every agent shares", and Alembic has neither. So are her GlobalPolicies
        // and her Formatting, which govern how a CHANNEL TURN is formed — quick replies, rich
        // cards, turn continuation, markdown for a rendered surface. Alembic has no channel, no
        // Guard, no Classifier and no turn in that sense. Handing it rules
        // about things that do not exist in its world is the most direct way to manufacture the
        // non-local contradictions this whole project exists to avoid.
        sb.AppendLine(MorganaLayerHeader);
        sb.AppendLine();
        sb.AppendLine(morgana.Personality);
        sb.AppendLine();
        sb.AppendLine(MorganaLayerFooter);
        sb.AppendLine();

        sb.AppendLine(AlembicLayerHeader);
        sb.AppendLine();
        AppendSections(sb, doctrine);
        sb.AppendLine();

        sb.AppendLine(PassLayerHeader);
        sb.AppendLine();
        AppendSections(sb, pass);

        return sb.ToString();
    }

    /// <summary>
    /// Appends whichever of a prompt's four sections carry anything.
    /// </summary>
    private static void AppendSections(StringBuilder sb, Records.Prompt prompt)
    {
        foreach (string? section in new[] { prompt.Target, prompt.Personality, prompt.Instructions, prompt.Formatting })
        {
            if (string.IsNullOrWhiteSpace(section))
                continue;

            sb.AppendLine(section);
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Reads <c>alembic.json</c> out of this assembly.
    /// </summary>
    private static Records.Prompt[] LoadPrompts()
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
