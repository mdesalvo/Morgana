using System.Globalization;
using System.Text;
using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Models;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="ICoherenceService"/>: one call over the whole domain, answered as JSON.
/// JSON here and prose everywhere else in Alembic, because this output is a list to sort, count and render as a table rather than writing to read.
/// </summary>
public class CoherenceService : ICoherenceService
{
    /// <summary>The prompt in <c>alembic.json</c> that governs the coherence pass.</summary>
    private const string CoherencePromptId = "DomainValidator";

    /// <summary>The key under which that prompt lists the classes the pass weighs, Alembic's own and read by nothing outside this workbench.</summary>
    private const string AspectsPropertyName = "Aspects";

    /// <summary>Where the composed Instructions carry the classes asked for this run.</summary>
    private const string AspectsPlaceholder = "((aspects))";

    /// <summary>Where the composed Formatting carries the <c>kind</c> values those classes may answer with.</summary>
    private const string KindsPlaceholder = "((kinds))";

    /// <summary>Resolves the pass's prompt and the framework primer it reads first.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>Supplies the chat client, on the Efficiency tier like all of Alembic's model work.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>Records parse and provider failures, which reach the caller too.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the pass to its prompts and the model.</summary>
    /// <param name="alembicPromptService">Resolves the <c>DomainValidator</c> prompt from <c>alembic.json</c>.</param>
    /// <param name="llmService">Supplies the chat client on the Efficiency tier.</param>
    /// <param name="logger">Records parse and provider failures.</param>
    public CoherenceService(
        IAlembicPromptService alembicPromptService,
        ILanguageModelService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.llmService = llmService;
        this.logger = logger;
    }

    /// <summary>
    /// The classes of defect the pass can be asked for, read from its prompt on every access.
    /// A list held apart from the prompt could disagree with the prose the pass is composed from.
    /// </summary>
    public IReadOnlyList<CoherenceAspect> Aspects =>
        alembicPromptService.Resolve(CoherencePromptId)
            .GetAdditionalPropertyOrDefault<List<CoherenceAspect>>(AspectsPropertyName, []);

    /// <summary>
    /// Reviews the whole domain for the classes asked for; a model or parse failure comes back as a report carrying an error, never as an exception.
    /// The pass is advisory, so a client who cannot run it this once keeps working with the deterministic checks.
    /// </summary>
    /// <param name="draft">The whole domain to read at once: every intent and every agent's full prose and toolkit.</param>
    /// <param name="aspects">The classes asked for this run.</param>
    /// <param name="resolved">Findings already fixed this sitting, so a re-run does not report a defect back the moment its own fix is read.</param>
    /// <param name="cancellationToken">Cancels the underlying completion.</param>
    /// <returns>The findings the model reports; otherwise an empty list with an error explaining why none could be produced.</returns>
    public async Task<CoherenceReport> ReviewAsync(
        DomainDraft draft,
        IReadOnlyList<string> aspects,
        IReadOnlyList<ResolvedCoherenceFinding>? resolved = null,
        CancellationToken cancellationToken = default)
    {
        // Two agents is the floor: with one there is nothing for a relational pass to relate and
        // running it anyway would spend a model call to say so.
        if (draft.Agents.Count < 2)
            return new CoherenceReport([], "A coherence pass needs at least two agents: everything it looks for is a relation between them.");

        // The pass's prose, its class catalogue among its properties.
        Records.Prompt coherence = alembicPromptService.Resolve(CoherencePromptId);

        // Only the classes asked for and the pass is composed of them: the prose of an unselected
        // class is not sent at all, rather than sent with a line asking the model to ignore it. A
        // rule a prompt states and then withdraws is read as a rule with an exception and the
        // exception is the part a model gets wrong.
        List<CoherenceAspect> asked =
            [.. coherence.GetAdditionalPropertyOrDefault<List<CoherenceAspect>>(AspectsPropertyName, [])
                    .Where(aspect => aspects.Contains(aspect.Id, StringComparer.OrdinalIgnoreCase))];

        // An empty selection asks no question, so no model call is spent on it.
        if (asked.Count == 0)
            return new CoherenceReport([], "Nothing was asked for: tick at least one thing to look for.");

        // The Instructions carry the asked classes' own prose and nothing of the others.
        string instructions = (coherence.Instructions ?? string.Empty)
            .Replace(AspectsPlaceholder, string.Join("\n\n", asked.Select(aspect => aspect.Description)), StringComparison.Ordinal);

        // The Formatting names the kinds the answer may use, which are exactly the classes asked for.
        string formatting = (coherence.Formatting ?? string.Empty)
            .Replace(KindsPlaceholder, string.Join(", ", asked.Select(aspect => $"`{aspect.Id}`")), StringComparison.Ordinal);

        // The framework first, this pass's own prose under it. Every class this pass reports turns
        // on a rule settled above the agent (prose restating one, a boundary the framework already
        // draws, a colleague relation the framework already governs) while a reader who has not been
        // told what those rules are is left judging by resemblance.
        string system = string.Join("\n\n",
            new[]
            {
                await alembicPromptService.ComposeFrameworkPrimerAsync(),
                Records.Prompt.Labeled(Constants.SectionLabels.Target, coherence.Target),
                Records.Prompt.Labeled(Constants.SectionLabels.Instructions, instructions),
                Records.Prompt.Labeled(Constants.SectionLabels.Formatting, formatting)
            }.Where(section => !string.IsNullOrWhiteSpace(section)));

        // Judging meaning across a whole domain needs the capable model the interview runs on.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Efficiency);

        try
        {
            // The whole domain in one request, resumed until the pass finishes its list.
            string answer = await StreamedCompletion.RunAsync(
                chatClient, system, Describe(draft, resolved),
                length => logger.LogInformation("The coherence pass was cut at {Length} characters; resuming", length),
                length => logger.LogWarning("The coherence pass went silent after {Length} characters; retrying once", length),
                cancellationToken);

            // An empty answer is told as one rather than shown as a clean domain.
            if (answer.Length == 0)
                return new CoherenceReport([], "The model returned nothing.");

            // The findings in the shape the Formatting asks for.
            CoherenceFinding[]? findings = JsonSerializer.Deserialize<CoherenceFinding[]>(
                answer, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            // A JSON null reads as nothing found.
            return new CoherenceReport(findings ?? []);
        }
        catch (JsonException ex)
        {
            // An answer that is not JSON leaves the client with the deterministic checks and an explanation.
            logger.LogWarning(ex, "The coherence pass did not answer as JSON");
            return new CoherenceReport([], $"The pass did not answer in the agreed shape: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A provider failure is reported on the page the same way; a cancellation is the client's own and goes through.
            logger.LogError(ex, "The coherence pass failed");
            return new CoherenceReport([], $"The pass could not run: {ex.Message}");
        }
    }

    /// <summary>
    /// States the whole domain in its exact words: every intent description, every agent's prose, every toolkit.
    /// The defects this pass exists for live in the phrasing, which a summary would smooth away before the model saw it.
    /// </summary>
    /// <param name="draft">The domain to render, read whole.</param>
    /// <param name="resolved">Prior fixes this sitting, rendered first so the model can tell a lingering defect from one already dealt with.</param>
    private static string Describe(DomainDraft draft, IReadOnlyList<ResolvedCoherenceFinding>? resolved)
    {
        StringBuilder sb = new StringBuilder();

        // Read first and named as what it is: this pass has no memory of its own last run, so
        // without this a client who fixed something and asked to run it again would face a pass
        // with no way to tell "still there" from "reads like something already dealt with" and
        // it would just see the domain and say what it always says about it.
        if (resolved is { Count: > 0 })
        {
            sb.AppendLine("# Already addressed this sitting");
            sb.AppendLine();
            sb.AppendLine("Do not report these again on sight. Read the current text below first: report one only if it is genuinely still true of it, not because it once resembled this.");
            sb.AppendLine();

            // Each fix with the finding it answered, in the applier's own words.
            foreach (ResolvedCoherenceFinding resolvedFinding in resolved)
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {resolvedFinding.Finding.Kind} ({resolvedFinding.Finding.Where}): {resolvedFinding.Finding.What} — addressed: {resolvedFinding.Resolution}");

            sb.AppendLine();
        }

        // The intents as the classifier weighs them, side by side, which is where two of them collide.
        sb.AppendLine("# Intents, as the classifier reads them");
        sb.AppendLine();

        foreach (IntentDraft intent in draft.Intents)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {intent.Name}: {intent.Description}");

        sb.AppendLine();
        sb.AppendLine("# Agents");
        sb.AppendLine();

        // Every agent below is described by its native tools alone. A Morgana agent may also reach MCP tools
        // at runtime, a C# fact (AgentCodeFacts.MCPServers) that agents.json never carries, so a capability
        // promised with no native tool listed for it is not on its own a promise without its tool.
        sb.AppendLine("Native tools only, below: an agent may also reach further capability through MCP servers this description cannot see, whether or not it lists any native tools of its own.");

        // One section per agent, in the domain's own order.
        foreach (AgentDraft agent in draft.Agents)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {agent.ID}");
            sb.AppendLine();

            // Territory rides with the four an agent reads about itself, though nobody but a
            // colleague ever reads it: an agent claiming a territory its own Instructions refuse is a
            // contradiction visible only when the two are set side by side, which is this pass's job.
            foreach (string? section in new[] { agent.Target, agent.Territory, agent.Instructions, agent.Personality, agent.Formatting })
                if (!string.IsNullOrWhiteSpace(section))
                    sb.AppendLine(section);

            // Nowhere in the four sections and the whole point of one class of finding: an agent's
            // colleagues live in its C#, so a boundary sentence refusing a subject its colleague
            // answers is invisible from the prose alone.
            sb.AppendLine();
            sb.AppendLine(agent.Code.Consults.Count > 0
                ? $"May consult and is handed each as a function of its own: {PeerNaming.Describe(agent.Code.Consults)}."
                : "Declares no colleagues: it can put a question to no other agent.");

            // No toolkit to list, said in so many words so the pass does not read an omission.
            if (agent.Tools.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("Declares no native tools.");
                continue;
            }

            sb.AppendLine();
            sb.AppendLine("Tools:");

            // Each tool with its parameters' scope and sharing, where two toolkits reaching one system under two shapes show.
            foreach (ToolDraft tool in agent.Tools)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {tool.Name}: {tool.Description}");

                foreach (ToolParameterDraft parameter in tool.Parameters)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {parameter.Name} [{parameter.Scope ?? "authored"}{(parameter.Shared ? ", shared" : "")}]: {parameter.Description}");
            }
        }

        return sb.ToString();
    }
}