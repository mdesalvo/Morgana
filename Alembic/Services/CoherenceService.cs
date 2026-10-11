using System.Globalization;
using System.Text;
using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Models;
using Alembic.Tools;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// Reads the whole domain in one call and reports the relational defects between its agents as JSON.
/// </summary>
/// <remarks>The answer is JSON because the findings are sorted and counted in a table.</remarks>
public class CoherenceService : ICoherenceService
{
    /// <summary>The <c>alembic.json</c> prompt that composes the coherence pass.</summary>
    private const string CoherencePromptId = "DomainValidator";

    /// <summary>The <c>alembic.json</c> prompt that holds the sentence resuming a cut-off answer.</summary>
    private const string AlembicPromptId = "Alembic";

    /// <summary>The key under which the validator prompt lists the classes of defect the client may ask for.</summary>
    private const string AspectsPropertyName = "Aspects";

    /// <summary>Marks where the Instructions carry the classes asked for in this run.</summary>
    private const string AspectsPlaceholder = "((aspects))";

    /// <summary>Marks where the Formatting carries the <c>kind</c> values those classes may answer with.</summary>
    private const string KindsPlaceholder = "((kinds))";

    /// <summary>Resolves the validator's prompt and the primer from <c>alembic.json</c>.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>Supplies the Efficiency-tier chat client that runs the pass.</summary>
    private readonly ILLMService llmService;

    /// <summary>Records a pass that failed or answered out of shape.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the pass over Alembic's prompts and the Efficiency-tier model.
    /// </summary>
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
    public IReadOnlyList<CoherenceAspect> Aspects =>
        alembicPromptService.Resolve(CoherencePromptId)
            .GetAdditionalPropertyOrDefault<List<CoherenceAspect>>(AspectsPropertyName, []);

    /// <inheritdoc />
    /// <remarks>A failure comes back as an explanatory <see cref="CoherenceReport.Error"/>, since the pass is advisory.</remarks>
    /// <param name="draft">The whole domain, read at once.</param>
    /// <param name="aspects">The classes of defect the client ticked.</param>
    /// <param name="resolved">Findings fixed earlier this sitting, shown so a re-run does not report them again.</param>
    /// <param name="cancellationToken">Stops the pass when the client leaves the page.</param>
    public async Task<CoherenceReport> ReviewAsync(
        DomainDraft draft,
        IReadOnlyList<string> aspects,
        IReadOnlyList<ResolvedCoherenceFinding>? resolved = null,
        CancellationToken cancellationToken = default)
    {
        // Every defect is a relation between agents: a domain of fewer than two has nothing to relate.
        if (draft.Agents.Count < 2)
            return new CoherenceReport([], "A coherence pass needs at least two agents: everything it looks for is a relation between them.");

        // The validator's own prose is read from alembic.json.
        Records.Prompt coherence = alembicPromptService.Resolve(CoherencePromptId);

        // Only the ticked classes are kept: an unselected class is absent from the prompt, since a rule stated and then withdrawn is read as a rule with an exception.
        List<CoherenceAspect> selection =
            [.. coherence.GetAdditionalPropertyOrDefault<List<CoherenceAspect>>(AspectsPropertyName, [])
                    .Where(a => aspects.Contains(a.Id, StringComparer.OrdinalIgnoreCase))];

        // A pass with nothing ticked has nothing to look for.
        if (selection.Count == 0)
            return new CoherenceReport([], "Nothing was asked for: tick at least one thing to look for.");

        // The Instructions carry the prose of each ticked class.
        string instructions = (coherence.Instructions ?? string.Empty)
            .Replace(AspectsPlaceholder, string.Join("\n\n", selection.Select(a => a.Description)), StringComparison.Ordinal);

        // The Formatting names the kind values the ticked classes may answer with.
        string formatting = (coherence.Formatting ?? string.Empty)
            .Replace(KindsPlaceholder, string.Join(", ", selection.Select(a => $"`{a.Id}`")), StringComparison.Ordinal);

        // The primer comes first: each class turns on a rule settled above the agent and a reader never told those rules judges by resemblance.
        string system = string.Join("\n\n",
            new[]
            {
                await alembicPromptService.ComposeFrameworkPrimerAsync(),
                Records.Prompt.Labeled(Constants.SectionLabels.Target, coherence.Target),
                Records.Prompt.Labeled(Constants.SectionLabels.Instructions, instructions),
                Records.Prompt.Labeled(Constants.SectionLabels.Formatting, formatting)
            }.Where(section => !string.IsNullOrWhiteSpace(section)));

        // The judgement of prose that must not contradict itself runs on the Efficiency tier.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Efficiency);

        try
        {
            // The model answers the whole domain at once and is resumed when the provider cuts the report short.
            string answer = await StreamedCompletion.RunAsync(
                chatClient, system, Describe(draft, resolved),
                alembicPromptService.ResolvePromptInjection(AlembicPromptId, "ContinuationRequest"),
                length => logger.LogInformation("The coherence pass was cut at {Length} characters; resuming", length),
                length => logger.LogWarning("The coherence pass went silent after {Length} characters; retrying once", length),
                cancellationToken);

            // An empty answer is a failed run, not a clean domain.
            if (answer.Length == 0)
                return new CoherenceReport([], "The model returned nothing.");

            // The answer is the list of findings in the shape the Formatting asks for.
            CoherenceFinding[]? findings = JsonSerializer.Deserialize<CoherenceFinding[]>(
                answer, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            // A literal null in the answer reads as no findings.
            return new CoherenceReport(findings ?? []);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The coherence pass did not answer as JSON");

            // The client is told the answer was out of shape.
            return new CoherenceReport([], $"The pass did not answer in the agreed shape: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The coherence pass failed");

            // The client is told why the pass could not run.
            return new CoherenceReport([], $"The pass could not run: {ex.Message}");
        }
    }

    /// <summary>
    /// Fetches a sentence of the report's framing from the validator prompt's <c>PromptInjections</c>.
    /// </summary>
    private string Said(string injectionName, params (string Placeholder, string Value)[] values) =>
        alembicPromptService.ResolvePromptInjection(CoherencePromptId, injectionName, values);

    /// <summary>
    /// States the whole domain: every intent description, every agent's prose, every toolkit.
    /// </summary>
    /// <remarks>The words are exact and never summarized: an overlap lives in the phrasing.</remarks>
    /// <param name="draft">The domain to render.</param>
    /// <param name="corrections">Fixes made earlier this sitting, listed first.</param>
    private string Describe(DomainDraft draft, IReadOnlyList<ResolvedCoherenceFinding>? corrections)
    {
        // The report is built top down: corrections, intents, then each agent.
        StringBuilder sb = new StringBuilder();

        // The pass keeps no memory of its last run: earlier fixes are listed first so a defect still present differs from one already dealt with.
        if (corrections is { Count: > 0 })
        {
            sb.AppendLine(Said("AddressedHeader"));
            sb.AppendLine();
            sb.AppendLine(Said("AddressedInstruction"));
            sb.AppendLine();

            // Each fix is shown with the finding it answered and what was done.
            foreach (ResolvedCoherenceFinding r in corrections)
                sb.AppendLine(Said("AddressedFinding",
                    ("kind", r.Finding.Kind), ("where", r.Finding.Where), ("what", r.Finding.What), ("resolution", r.Resolution)));

            sb.AppendLine();
        }

        // The map comes next since the classifier weighs every description against the others.
        sb.AppendLine(Said("IntentsHeader"));
        sb.AppendLine();

        // Each intent is shown with its routing description exactly as written.
        foreach (IntentDraft intent in draft.Intents)
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {intent.Name}: {intent.Description}");

        sb.AppendLine();
        sb.AppendLine(Said("AgentsHeader"));
        sb.AppendLine();

        // An agent may reach MCP tools that only its C# knows, so a promise with no native tool behind it may still be backed.
        sb.AppendLine(Said("NativeToolsOnly"));

        // Each agent is rendered whole: its sections, its colleagues, its workflows and its toolkit.
        foreach (AgentDraft agent in draft.Agents)
        {
            sb.AppendLine();
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {agent.ID}");
            sb.AppendLine();

            // The territory sits beside the sections the agent reads about itself: a territory its own Instructions refuse shows only side by side.
            foreach (string? section in new[] { agent.Target, agent.Territory, agent.Instructions, agent.Personality, agent.Formatting })
                if (!string.IsNullOrWhiteSpace(section))
                    sb.AppendLine(section);

            // The colleagues live in the agent's C#, so a boundary refusing a subject a colleague answers is invisible from the prose alone.
            sb.AppendLine();
            sb.AppendLine(agent.Code.Consults.Count > 0
                ? Said("ConsultsDeclared", ("colleagues", PeerNaming.Describe(agent.Code.Consults)))
                : Said("ConsultsNone"));

            // A procedure written in the prose that a workflow already imposes shows only with the workflow beside the sentence.
            sb.AppendLine();
            sb.AppendLine(InterviewTools.DescribeWorkflows(alembicPromptService, agent.Workflows));

            // An agent with no native tool has no toolkit section.
            if (agent.Tools.Count == 0)
            {
                sb.AppendLine();
                sb.AppendLine(Said("NoNativeTools"));
                continue;
            }

            sb.AppendLine();
            sb.AppendLine(Said("ToolsHeader"));

            // Each tool is shown with its description and the scope of every parameter it fills.
            foreach (ToolDraft tool in agent.Tools)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {tool.Name}{(tool.RequiresExecutionApproval ? " " + Said("ToolWaitsForApproval") : string.Empty)}: {tool.Description}");

                foreach (ToolParameterDraft parameter in tool.Parameters)
                    sb.AppendLine(CultureInfo.InvariantCulture, $"    {parameter.Name} [{parameter.Scope ?? Said("NoScopeYet")}{(parameter.Shared ? ", " + Said("ParameterShared") : "")}]: {parameter.Description}");
            }
        }

        // The report hands the pass the domain's exact words in one text.
        return sb.ToString();
    }
}
