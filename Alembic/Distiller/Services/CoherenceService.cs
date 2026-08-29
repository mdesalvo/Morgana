using System.Text;
using System.Text.Json;
using Distiller.Interfaces;
using Distiller.Model;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Distiller.Services;

/// <summary>
/// Default <see cref="ICoherenceService"/>: one call over the whole domain, answered as JSON.
/// </summary>
/// <remarks>
/// JSON here and prose everywhere else in Alembic, because this output is a list to be sorted,
/// counted and rendered in a table rather than read as writing. The interview went the other way for
/// the same reason: there the reply <em>is</em> the product.
/// </remarks>
public class CoherenceService : ICoherenceService
{
    /// <summary>
    /// The prompt in <c>alembic.json</c> that governs the coherence pass.
    /// </summary>
    private const string CoherencePromptId = "DomainValidator";

    /// <summary>
    /// Where the composed Instructions carry the classes asked for this run.
    /// </summary>
    private const string AspectsPlaceholder = "((aspects))";

    /// <summary>
    /// Where the composed Formatting carries the <c>kind</c> values those classes may answer with.
    /// </summary>
    private const string KindsPlaceholder = "((kinds))";

    private readonly IAlembicPromptService alembicPromptService;
    private readonly ILLMService llmService;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the coherence service.
    /// </summary>
    /// <param name="alembicPromptService">Resolves the <c>DomainValidator</c> prompt from <c>alembic.json</c>.</param>
    /// <param name="llmService">Supplies the chat client, always on the Performance tier.</param>
    /// <param name="logger">Used to record parse and provider failures — both surface to the caller too.</param>
    public CoherenceService(
        IAlembicPromptService alembicPromptService,
        ILLMService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.llmService = llmService;
        this.logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resolved on every read rather than cached: it is a parse of an embedded resource, and a list
    /// held twice is a list that can disagree with the prose the pass is actually composed from.
    /// </remarks>
    public IReadOnlyList<CoherenceAspect> Aspects =>
        alembicPromptService.Resolve(CoherencePromptId)
            .GetAdditionalPropertyOrDefault<List<CoherenceAspect>>("Aspects", []);

    /// <inheritdoc />
    /// <remarks>
    /// Never throws on a model or parsing failure — every catch below returns a <see cref="CoherenceReport"/>
    /// carrying an explanatory <see cref="CoherenceReport.Error"/> instead, because this pass is
    /// advisory: a client who cannot run it this once should still be able to keep working with the
    /// deterministic checks <c>DraftValidationService</c> already gave them.
    /// </remarks>
    /// <param name="draft">The whole domain to read at once — every intent and every agent's full prose and toolkit.</param>
    /// <param name="resolved">Findings already fixed earlier this sitting, so a re-run does not
    /// report the same defect back the moment its own fix lands in front of the model again.</param>
    /// <param name="cancellationToken">Cancels the underlying completion.</param>
    /// <returns>The findings the model reports, or an empty list with an <see cref="CoherenceReport.Error"/>
    /// explaining why none could be produced.</returns>
    public async Task<CoherenceReport> ReviewAsync(
        DomainDraft draft,
        IReadOnlyList<string> aspects,
        IReadOnlyList<ResolvedCoherenceFinding>? resolved = null,
        CancellationToken cancellationToken = default)
    {
        // Two agents is the floor: with one there is nothing for a relational pass to relate, and
        // running it anyway would spend a Performance call to say so.
        if (draft.Agents.Count < 2)
            return new CoherenceReport([], "A coherence pass needs at least two agents: everything it looks for is a relation between them.");

        Records.Prompt coherence = alembicPromptService.Resolve(CoherencePromptId);

        // Only the classes asked for, and the pass is composed of them: the prose of an unselected
        // class is not sent at all, rather than sent with a line asking the model to ignore it. A
        // rule a prompt states and then withdraws is read as a rule with an exception, and the
        // exception is the part a model gets wrong.
        List<CoherenceAspect> asked =
            [.. coherence.GetAdditionalPropertyOrDefault<List<CoherenceAspect>>("Aspects", [])
                    .Where(a => aspects.Contains(a.Id, StringComparer.OrdinalIgnoreCase))];

        if (asked.Count == 0)
            return new CoherenceReport([], "Nothing was asked for: tick at least one thing to look for.");

        string instructions = (coherence.Instructions ?? string.Empty)
            .Replace(AspectsPlaceholder, string.Join("\n\n", asked.Select(a => a.Description)), StringComparison.Ordinal);

        string formatting = (coherence.Formatting ?? string.Empty)
            .Replace(KindsPlaceholder, string.Join(", ", asked.Select(a => $"`{a.Id}`")), StringComparison.Ordinal);

        string system = string.Join("\n\n",
            new[] { coherence.Target, instructions, formatting }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Performance);

        try
        {
            string answer = await StreamedCompletion.RunAsync(
                chatClient, system, Describe(draft, resolved),
                length => logger.LogInformation("The coherence pass was cut at {Length} characters; resuming", length),
                length => logger.LogWarning("The coherence pass went silent after {Length} characters; retrying once", length),
                cancellationToken);

            if (answer.Length == 0)
                return new CoherenceReport([], "The model returned nothing.");

            CoherenceFinding[]? findings = JsonSerializer.Deserialize<CoherenceFinding[]>(
                answer, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return new CoherenceReport(findings ?? []);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The coherence pass did not answer as JSON");
            return new CoherenceReport([], $"The pass did not answer in the agreed shape: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The coherence pass failed");
            return new CoherenceReport([], $"The pass could not run: {ex.Message}");
        }
    }

    /// <summary>
    /// States the whole domain: every intent description, every agent's prose, every toolkit.
    /// </summary>
    /// <remarks>
    /// All of it, and not a summary. The defects this pass exists for live in the exact words —
    /// two descriptions that overlap do so in their phrasing, and a summary is precisely the step
    /// that would smooth the overlap away before the model ever sees it.
    /// </remarks>
    /// <param name="draft">The domain to render — read whole, never summarized.</param>
    /// <param name="resolved">Prior fixes this sitting, rendered first and by name so the model can
    /// tell a lingering defect from one it already dealt with.</param>
    private static string Describe(DomainDraft draft, IReadOnlyList<ResolvedCoherenceFinding>? resolved)
    {
        StringBuilder sb = new StringBuilder();

        // Read first, and named as what it is: this pass has no memory of its own last run, so
        // without this a client who fixed something and asked to run it again would face a pass
        // with no way to tell "still there" from "reads like something already dealt with" — it
        // would just see the domain and say what it always says about it.
        if (resolved is { Count: > 0 })
        {
            sb.AppendLine("# Already addressed this sitting");
            sb.AppendLine();
            sb.AppendLine("Do not report these again on sight. Read the current text below first: report one only if it is genuinely still true of it, not because it once resembled this.");
            sb.AppendLine();

            foreach (ResolvedCoherenceFinding r in resolved)
                sb.AppendLine($"- {r.Finding.Kind} ({r.Finding.Where}): {r.Finding.What} — addressed: {r.Resolution}");

            sb.AppendLine();
        }

        sb.AppendLine("# Intents, as the classifier reads them");
        sb.AppendLine();

        foreach (IntentDraft intent in draft.Intents)
            sb.AppendLine($"- {intent.Name}: {intent.Description}");

        sb.AppendLine();
        sb.AppendLine("# Agents");
        sb.AppendLine();
        // Every agent below is described by its native tools alone. A Morgana agent may also be
        // hybrid — native tools of its own plus arbitrary MCP tools reached at runtime — and which
        // MCP servers an agent reaches is a C# fact (AgentCodeFacts.MCPServers) agents.json never
        // carries, whether that agent's own tool count here is zero or not. So a capability a
        // Target or Instructions promises with no native tool listed for it is not evidence of
        // PROMISES WITHOUT TOOLS on its own — it may be MCP-backed, invisibly to this description.
        sb.AppendLine("Native tools only, below: an agent may also reach further capability through MCP servers this description cannot see, whether or not it lists any native tools of its own.");

        foreach (AgentDraft agent in draft.Agents)
        {
            sb.AppendLine();
            sb.AppendLine($"## {agent.ID}");
            sb.AppendLine();

            foreach (string? section in new[] { agent.Target, agent.Instructions, agent.Personality, agent.Formatting })
                if (!string.IsNullOrWhiteSpace(section))
                    sb.AppendLine(section);

            // Nowhere in the four sections, and the whole point of one class of finding: an agent's
            // colleagues live in its C#, so a boundary sentence refusing a subject its colleague
            // answers is invisible from the prose alone.
            sb.AppendLine();
            sb.AppendLine(agent.Code.Consults.Count > 0
                ? $"May consult, and is handed each as a function of its own: {string.Join(", ", agent.Code.Consults)}."
                : "Declares no colleagues: it can put a question to no other agent.");

            if (agent.Tools.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine("Declares no native tools.");
                continue;
            }

            sb.AppendLine();
            sb.AppendLine("Tools:");

            foreach (ToolDraft tool in agent.Tools)
            {
                sb.AppendLine($"- {tool.Name}: {tool.Description}");

                foreach (ToolParameterDraft parameter in tool.Parameters)
                    sb.AppendLine($"    {parameter.Name} [{parameter.Scope ?? "authored"}{(parameter.Shared ? ", shared" : "")}]: {parameter.Description}");
            }
        }

        return sb.ToString();
    }
}
