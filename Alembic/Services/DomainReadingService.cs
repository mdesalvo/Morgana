using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alembic.Interfaces;
using Alembic.Models;
using Microsoft.Extensions.AI;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IDomainReadingService"/>: one call over the whole uploaded domain, answered as JSON.
/// What the shop does is one subject, so it is read once per upload rather than per agent, which would write the same sentence about three of them;
/// any step of the interview then opens knowing the trade, even when the client goes straight to the third agent.
/// </summary>
public class DomainReadingService : IDomainReadingService
{
    /// <summary>The prompt in <c>alembic.json</c> that governs reading a finished domain.</summary>
    private const string ReaderPromptId = "DomainReader";

    /// <summary>Resolves the reader's prompt.</summary>
    private readonly IAlembicPromptService alembicPromptService;

    /// <summary>Supplies the chat client, on the Efficiency tier like all of Alembic's model work.</summary>
    private readonly ILanguageModelService llmService;

    /// <summary>Records provider and parse failures, which reach the caller too.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the reading to its prompt and the model.</summary>
    /// <param name="alembicPromptService">Resolves the <c>DomainReader</c> prompt from <c>alembic.json</c>.</param>
    /// <param name="llmService">Supplies the chat client on the Efficiency tier.</param>
    /// <param name="logger">Records provider and parse failures.</param>
    public DomainReadingService(
        IAlembicPromptService alembicPromptService,
        ILanguageModelService llmService,
        ILogger logger)
    {
        this.alembicPromptService = alembicPromptService;
        this.llmService = llmService;
        this.logger = logger;
    }

    /// <summary>
    /// Reads the uploaded domain and writes what it says about the client's work onto the Draft; never throws.
    /// An upload that could not be read leaves a working interview that simply has more questions to ask.
    /// </summary>
    public async Task<DomainReading> ReadAsync(DomainDraft draft, CancellationToken cancellationToken = default)
    {
        // A configuration with no agent has no prose to read a business out of.
        if (draft.Agents.Count == 0)
            return new DomainReading(0, "There is nothing to read: the upload carried no agent.");

        // The reader's own prose, which defines the JSON it answers.
        Records.Prompt reader = alembicPromptService.Resolve(ReaderPromptId);

        // A one-shot JSON answer speaks to nobody, so the reader carries no personality.
        string system = string.Join("\n\n",
            new[]
            {
                Records.Prompt.Labeled(Constants.SectionLabels.Target, reader.Target),
                Records.Prompt.Labeled(Constants.SectionLabels.Instructions, reader.Instructions),
                Records.Prompt.Labeled(Constants.SectionLabels.Formatting, reader.Formatting)
            }.Where(section => section.Length > 0));

        // Reading prose closely is the job the interview's tier is chosen for.
        IChatClient chatClient = llmService.GetChatClient(Records.LLMTier.Efficiency);

        try
        {
            // The whole domain in one request, resumed until the reader finishes its answer.
            string answer = await StreamedCompletion.RunAsync(
                chatClient, system, Describe(draft),
                length => logger.LogInformation("The domain reading was cut at {Length} characters; resuming", length),
                length => logger.LogWarning("The domain reading went silent after {Length} characters; retrying once", length),
                cancellationToken);

            // An empty answer teaches nothing and the client is told so.
            if (answer.Length == 0)
                return new DomainReading(0, "The model returned nothing.");

            // The answer in the shape the reader's Formatting asks for.
            Reading? reading = JsonSerializer.Deserialize<Reading>(
                answer, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            // A JSON null is no reading; anything else is filed onto the Draft and counted.
            return reading is null
                ? new DomainReading(0, "The reading did not come back in the agreed shape.")
                : new DomainReading(Keep(draft, reading));
        }
        catch (JsonException ex)
        {
            // An answer that is not JSON costs the head start and nothing else.
            logger.LogWarning(ex, "The domain reading did not answer as JSON");
            return new DomainReading(0, $"The reading did not answer in the agreed shape: {ex.Message}");
        }
        catch (Exception ex)
        {
            // A provider failure costs the head start too: the interview still works without it.
            logger.LogError(ex, "The domain reading failed");
            return new DomainReading(0, $"The upload could not be read: {ex.Message}");
        }
    }

    /// <summary>
    /// Writes what was read onto the Draft, every fact marked as read rather than said, counting what it wrote.
    /// Only where nothing is known yet: a reading never comes in over what the client said in an earlier sitting of the same file.
    /// </summary>
    private static int Keep(DomainDraft draft, Reading reading)
    {
        // How many facts were filed, which the result reports beside any error.
        int written = 0;

        // The business as a whole, unless the client already told Alembic about it.
        if (draft.Learned.Count == 0)
            foreach (ReadFact fact in Sentences(reading.Business))
            {
                // Inferred: the client never said it and every pass shows it as Alembic's own reading.
                draft.Learned.Add(new KnownFact(fact.Subject.Trim(), fact.Fact.Trim(), Inferred: true));
                written++;
            }

        // Each agent the reader wrote about, matched by the intent it answers to.
        foreach (AgentReading agentReading in reading.Agents ?? [])
        {
            AgentDraft? agent = draft.Agents.FirstOrDefault(candidate =>
                string.Equals(candidate.ID, agentReading.Intent, StringComparison.OrdinalIgnoreCase));

            // An intent the domain does not hold takes nothing, nor does an agent the client already spoke about.
            if (agent is null || agent.Known.Count > 0)
                continue;

            foreach (ReadFact fact in Sentences(agentReading.Facts))
            {
                // Inferred, as for the business.
                agent.Known.Add(new KnownFact(fact.Subject.Trim(), fact.Fact.Trim(), Inferred: true));
                written++;
            }
        }

        // The number of facts that now stand on the Draft as read.
        return written;
    }

    /// <summary>Whatever came back carrying both a subject and a sentence, without repeats.</summary>
    private static IEnumerable<ReadFact> Sentences(IReadOnlyList<ReadFact>? facts) =>
        (facts ?? [])
            .Where(fact => !string.IsNullOrWhiteSpace(fact.Subject) && !string.IsNullOrWhiteSpace(fact.Fact))
            .DistinctBy(fact => fact.Fact.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The uploaded domain as the reader meets it: every intent and every agent's prose, with its toolkit where it holds one.
    /// A parameter such as 'which branch' tells there is more than one shop where no prose says so; an agent just imported holds no toolkit yet.
    /// </summary>
    private static string Describe(DomainDraft draft)
    {
        // One heading per agent, under the intent name the reader's answer files its facts by.
        StringBuilder described = new StringBuilder();

        described.AppendLine("# The agents of this domain, by the intent name each answers to");
        described.AppendLine();

        // One section per agent, in the order the configuration declares them.
        foreach (AgentDraft agent in draft.Agents)
        {
            // The intent routing to the agent, whose description says which requests land on it.
            IntentDraft? intent = draft.Intents.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, agent.ID, StringComparison.OrdinalIgnoreCase));

            described.AppendLine(CultureInfo.InvariantCulture, $"## {agent.ID}");

            // Each line only where the configuration says something, so the reader never weighs an empty heading.
            if (intent?.Description is { Length: > 0 } routes)
                described.AppendLine(CultureInfo.InvariantCulture, $"- what lands here: {routes}");

            // The sections in plain words, without the markdown that would distract a reader looking for the trade.
            if (AgentRows.Plain(agent.Target) is { Length: > 0 } target)
                described.AppendLine(CultureInfo.InvariantCulture, $"- what it is for: {target}");

            if (AgentRows.Plain(agent.Territory) is { Length: > 0 } territory)
                described.AppendLine(CultureInfo.InvariantCulture, $"- what it is asked about: {territory}");

            if (AgentRows.Plain(agent.Instructions) is { Length: > 0 } instructions)
                described.AppendLine(CultureInfo.InvariantCulture, $"- how it goes about it: {instructions}");

            // Every tool with the values it is told, which name the things the business handles.
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

    /// <summary>What the reader answers: the shop as a whole, then each agent of it.</summary>
    private sealed record Reading(
        [property: JsonPropertyName("business")] List<ReadFact>? Business,
        [property: JsonPropertyName("agents")] List<AgentReading>? Agents);

    /// <summary>One agent of the answer, named by the intent it answers to.</summary>
    private sealed record AgentReading(
        [property: JsonPropertyName("intent")] string Intent,
        [property: JsonPropertyName("facts")] List<ReadFact>? Facts);

    /// <summary>One fact as the reader hands it over, filed under what it is about.</summary>
    private sealed record ReadFact(
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("fact")] string Fact);
}