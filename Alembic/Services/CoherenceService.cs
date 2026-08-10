using System.Text;
using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Model;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

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

    private readonly IAlembicPromptService alembicPromptService;
    private readonly ILLMService llmService;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the coherence service.
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
    public async Task<CoherenceReport> ReviewAsync(DomainDraft draft, CancellationToken cancellationToken = default)
    {
        // Two agents is the floor: with one there is nothing for a relational pass to relate, and
        // running it anyway would spend a Performance call to say so.
        if (draft.Agents.Count < 2)
            return new CoherenceReport([], "A coherence pass needs at least two agents: everything it looks for is a relation between them.");

        Records.Prompt coherence = alembicPromptService.Resolve(CoherencePromptId);

        string system = string.Join("\n\n",
            new[] { coherence.Target, coherence.Instructions, coherence.Formatting }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Performance);

        try
        {
            string answer = await StreamedCompletion.RunAsync(
                chatClient, system, Describe(draft),
                length => logger.LogInformation("The coherence pass was cut at {Length} characters; resuming", length),
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
    private static string Describe(DomainDraft draft)
    {
        StringBuilder sb = new StringBuilder();

        sb.AppendLine("# Intents, as the classifier reads them");
        sb.AppendLine();

        foreach (IntentDraft intent in draft.Intents)
            sb.AppendLine($"- {intent.Name}: {intent.Description}");

        sb.AppendLine();
        sb.AppendLine("# Agents");

        foreach (AgentDraft agent in draft.Agents)
        {
            sb.AppendLine();
            sb.AppendLine($"## {agent.ID}");
            sb.AppendLine();

            foreach (string? section in new[] { agent.Target, agent.Instructions, agent.Personality, agent.Formatting })
                if (!string.IsNullOrWhiteSpace(section))
                    sb.AppendLine(section);

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
