using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Distiller.Interfaces;
using Distiller.Model;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Distiller.Services;

/// <summary>
/// Default <see cref="IDomainReadingService"/>: one call over the uploaded domain, answered as JSON.
/// </summary>
/// <remarks>
/// Once per upload rather than once per step, and over the whole domain rather than one agent at a
/// time: what the shop does is one subject and a desk read on its own invites the same sentence
/// being written about three of them. The interview then opens on any entry of it already knowing
/// the trade, which is the whole point — the client may go straight to correcting the third agent
/// and never pass through the ones that would have taught Alembic anything.
/// </remarks>
public class DomainReadingService : IDomainReadingService
{
    /// <summary>
    /// The prompt in <c>alembic.json</c> that governs reading a finished domain.
    /// </summary>
    private const string ReaderPromptId = "DomainReader";

    private readonly IAlembicPromptService alembicPromptService;
    private readonly ILLMService llmService;
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the reading service.
    /// </summary>
    /// <param name="alembicPromptService">Resolves the <c>DomainReader</c> prompt from <c>alembic.json</c>.</param>
    /// <param name="llmService">Supplies the chat client, always on the Performance tier.</param>
    /// <param name="logger">Records provider and parse failures, which surface to the caller too.</param>
    public DomainReadingService(
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
    /// Never throws: an upload that could not be read leaves the client exactly where they were
    /// before this existed, which is a working interview that has to ask more questions.
    /// </remarks>
    public async Task<DomainReading> ReadAsync(DomainDraft draft, CancellationToken cancellationToken = default)
    {
        if (draft.Agents.Count == 0)
            return new DomainReading(0, "There is nothing to read: the upload carried no agent.");

        Records.Prompt reader = alembicPromptService.Resolve(ReaderPromptId);

        string system = string.Join("\n\n",
            new[] { reader.Target, reader.Instructions, reader.Formatting }
                .Where(section => !string.IsNullOrWhiteSpace(section)));

        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Performance);

        try
        {
            string answer = await StreamedCompletion.RunAsync(
                chatClient, system, Describe(draft),
                length => logger.LogInformation("The domain reading was cut at {Length} characters; resuming", length),
                length => logger.LogWarning("The domain reading went silent after {Length} characters; retrying once", length),
                cancellationToken);

            if (answer.Length == 0)
                return new DomainReading(0, "The model returned nothing.");

            Reading? reading = JsonSerializer.Deserialize<Reading>(
                answer, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            return reading is null
                ? new DomainReading(0, "The reading did not come back in the agreed shape.")
                : new DomainReading(Keep(draft, reading));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "The domain reading did not answer as JSON");
            return new DomainReading(0, $"The reading did not answer in the agreed shape: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The domain reading failed");
            return new DomainReading(0, $"The upload could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes what was read onto the Draft, every fact marked as read rather than said.
    /// </summary>
    /// <remarks>
    /// Only onto agents that hold nothing yet: a reading must never come in over what the client
    /// themselves said about a desk in an earlier sitting of the same file.
    /// </remarks>
    private static int Keep(DomainDraft draft, Reading reading)
    {
        int written = 0;

        if (draft.Learned.Count == 0)
            foreach (ReadFact fact in Sentences(reading.Business))
            {
                draft.Learned.Add(new KnownFact(fact.Subject.Trim(), fact.Fact.Trim(), Inferred: true));
                written++;
            }

        foreach (DeskReading desk in reading.Desks ?? [])
        {
            AgentDraft? agent = draft.Agents.FirstOrDefault(candidate =>
                string.Equals(candidate.ID, desk.Intent, StringComparison.OrdinalIgnoreCase));

            if (agent is null || agent.Known.Count > 0)
                continue;

            foreach (ReadFact fact in Sentences(desk.Facts))
            {
                agent.Known.Add(new KnownFact(fact.Subject.Trim(), fact.Fact.Trim(), Inferred: true));
                written++;
            }
        }

        return written;
    }

    /// <summary>Whatever came back carrying both a subject and a sentence, without repeats.</summary>
    private static IEnumerable<ReadFact> Sentences(IReadOnlyList<ReadFact>? facts) =>
        (facts ?? [])
            .Where(fact => !string.IsNullOrWhiteSpace(fact.Subject) && !string.IsNullOrWhiteSpace(fact.Fact))
            .DistinctBy(fact => fact.Fact.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The uploaded domain as the reader meets it: every intent, every agent's prose and toolkit.
    /// </summary>
    /// <remarks>
    /// The tools are in it because half of what a configuration says about a business is there: a
    /// parameter called 'which branch' says there is more than one shop and no section of prose
    /// anywhere says it.
    /// </remarks>
    private static string Describe(DomainDraft draft)
    {
        StringBuilder described = new StringBuilder();

        described.AppendLine("# The desks of this domain, by the intent name each answers to");
        described.AppendLine();

        foreach (AgentDraft agent in draft.Agents)
        {
            IntentDraft? intent = draft.Intents.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, agent.ID, StringComparison.OrdinalIgnoreCase));

            described.AppendLine($"## {agent.ID}");

            if (intent?.Description is { Length: > 0 } routes)
                described.AppendLine($"- what lands here: {routes}");

            if (AgentRows.Plain(agent.Target) is { Length: > 0 } target)
                described.AppendLine($"- what it is for: {target}");

            if (AgentRows.Plain(agent.ConsultMeFor) is { Length: > 0 } territory)
                described.AppendLine($"- what it is asked about: {territory}");

            if (AgentRows.Plain(agent.Instructions) is { Length: > 0 } instructions)
                described.AppendLine($"- how it goes about it: {instructions}");

            foreach (ToolDraft tool in agent.Tools)
                described.AppendLine(
                    $"- it can {tool.Name}: {tool.Description}"
                    + (tool.Parameters.Count == 0
                        ? string.Empty
                        : $" (told: {string.Join(", ", tool.Parameters.Select(parameter => parameter.Name))})"));

            described.AppendLine();
        }

        return described.ToString();
    }

    /// <summary>What the reader answers: the shop as a whole, then each desk of it.</summary>
    private sealed record Reading(
        [property: JsonPropertyName("business")] List<ReadFact>? Business,
        [property: JsonPropertyName("desks")] List<DeskReading>? Desks);

    /// <summary>One desk of the answer, named by the intent it answers to.</summary>
    private sealed record DeskReading(
        [property: JsonPropertyName("intent")] string Intent,
        [property: JsonPropertyName("facts")] List<ReadFact>? Facts);

    /// <summary>One fact as the reader hands it over, filed under what it is about.</summary>
    private sealed record ReadFact(
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("fact")] string Fact);
}
