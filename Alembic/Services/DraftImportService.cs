using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IDraftImportService"/>: parses an uploaded <c>agents.json</c> with Morgana's own record types and projects it onto a <see cref="DomainDraft"/>.
/// It reads the agents' prose and carries every AdditionalProperties key verbatim; the toolkit is the interview's to author, its contracts living in the client's C#.
/// </summary>
public class DraftImportService : IDraftImportService
{
    /// <summary>Case-insensitive like the framework's own reading of <c>agents.json</c>, so a file that loads in Morgana loads here.</summary>
    private static readonly JsonSerializerOptions ImportOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Records each import and each file that would not parse.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the import to its logger.</summary>
    /// <param name="logger">Records each import and each file that would not parse.</param>
    public DraftImportService(ILogger logger)
    {
        this.logger = logger;
    }

    /// <summary>
    /// Parses a bare <c>agents.json</c> into a Draft marked imported, with its baseline frozen beside it.
    /// Telling it apart from a save file and unwrapping a zip are the caller's, done by content and never by file name.
    /// </summary>
    /// <param name="agentsJson">The uploaded file's raw bytes, positioned at its start.</param>
    /// <param name="fileName">The name the client uploaded it under, kept for <see cref="DomainDraft.ImportedFrom"/> and the log and never parsed for meaning.</param>
    /// <param name="cancellationToken">Cancels the deserialization.</param>
    /// <returns>The parsed Draft plus any notices worth surfacing; otherwise a Draft-less result carrying the one error that explains why.</returns>
    public async Task<DraftImportResult> ImportAsync(
        Stream agentsJson,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        // The file in the framework's own shape; nothing when it is not one.
        AgentsConfigurationFile? configuration;

        try
        {
            // Read with Morgana's own records, so what parses here is what Morgana would load.
            configuration = await JsonSerializer.DeserializeAsync<AgentsConfigurationFile>(
                agentsJson, ImportOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            // The parser's own message names where the file broke, which is what the client needs to fix it.
            logger.LogWarning(ex, "Could not parse the uploaded agents.json ({FileName})", fileName);
            return new DraftImportResult(null, [], $"This is not a readable agents.json: {ex.Message}");
        }

        // A file holding the JSON literal null carries no domain.
        if (configuration is null)
            return new DraftImportResult(null, [], "The uploaded file is empty.");

        // What the client is told about the upload, filled as the agents are read.
        List<string> notices = [];

        DomainDraft draft = new DomainDraft
        {
            ImportedFrom = fileName,
            Intents = [.. configuration.Intents.Select(ToIntentDraft)],
            Agents = [.. configuration.Agents.Select(agent => ToAgentDraft(agent, notices))],

            // Frozen here, before anything can touch it and by a second projection of the same parsed
            // configuration rather than a copy of what was just built: the baseline is the file and
            // deriving it the same way guarantees the migration report can only ever show a real edit.
            Baseline = new DomainDraft
            {
                ImportedFrom = fileName,
                Intents = [.. configuration.Intents.Select(ToIntentDraft)],
                Agents = [.. configuration.Agents.Select(agent => ToAgentDraft(agent, []))]
            }
        };

        // On the working draft only. The baseline stays the file as it arrived, so what the client is
        // told here is a change to their configuration rather than a quiet difference between what
        // they uploaded and what they will download.
        if (draft.DropFallbackIntent())
            notices.Add($"This configuration declared the '{DomainDraft.FallbackIntent}' intent, which has been removed: "
                        + "it is the complement of your domain rather than a part of it, so Morgana's classifier now carries it "
                        + "and describes it in its own prompt. An installation ignores such a declaration in any case.");

        logger.LogInformation(
            "Imported {IntentCount} intents and {AgentCount} agents from {FileName}",
            draft.Intents.Count, draft.Agents.Count, fileName);

        // The Draft with every notice the reading produced.
        return new DraftImportResult(draft, notices, null);
    }

    /// <summary>Projects an intent definition onto its Draft element, marked imported.</summary>
    /// <param name="intent">One entry of the uploaded configuration's <c>Intents</c> array.</param>
    private static IntentDraft ToIntentDraft(Records.IntentDefinition intent) => new()
    {
        Name = intent.Name,
        Description = intent.Description,
        Label = intent.Label,
        DefaultValue = intent.DefaultValue,
        Origin = Provenance.Imported
    };

    /// <summary>Projects an agent prompt onto its Draft element, keeping every AdditionalProperties entry untouched.</summary>
    /// <param name="prompt">One entry of the uploaded configuration's <c>Agents</c> array, in the framework's own record shape.</param>
    /// <param name="notices">
    /// Receives one notice per key Alembic does not model; the baseline's projection passes an empty list, so one upload reports each key once.
    /// </param>
    private AgentDraft ToAgentDraft(Records.Prompt prompt, List<string> notices)
    {
        // Every key is carried as read, since the toolkit does not live in agents.json. Entries that held
        // nothing disappear rather than being written back empty.
        List<Dictionary<string, object>> unmodelled =
            [.. prompt.AdditionalProperties
                      .Select(entry => entry.ToDictionary(pair => pair.Key, pair => pair.Value))
                      .Where(entry => entry.Count > 0)];

        // The client is told which keys travel through untouched because Alembic has no use for them.
        notices.AddRange(
            unmodelled.SelectMany(entry => entry.Keys)
                      .Select(key => $"Agent '{prompt.ID}' declares '{key}', which Alembic does not model. It will be written back exactly as it was read."));

        // The toolkit is left empty: its contracts live in the client's C# and the interview authors them again.
        return new AgentDraft
        {
            ID = prompt.ID,
            Target = prompt.Target,
            Territory = prompt.Territory,
            Instructions = prompt.Instructions,
            Formatting = prompt.Formatting,
            Personality = prompt.Personality,
            Language = prompt.Language,
            Version = prompt.Version,
            Tools = [],
            UnmodelledProperties = unmodelled,
            Code = InferCodeFacts(prompt.ID),
            Origin = Provenance.Imported
        };
    }

    /// <summary>
    /// Proposes the C# facts an <c>agents.json</c> cannot carry, flagged <see cref="AgentCodeFacts.Inferred"/> for the client to confirm.
    /// Only the agent class name follows from the file, by the framework's naming convention; namespace and tier stay null, since a confident wrong value
    /// is worse than an empty one the interview will ask about.
    /// </summary>
    /// <param name="agentId">The intent this agent answers, the only thing the naming convention has to go on.</param>
    private static AgentCodeFacts InferCodeFacts(string agentId)
    {
        // The tool class stays unnamed: the emit falls back to the naming convention when it writes one.
        return new AgentCodeFacts
        {
            AgentClassName = AgentCodeFacts.ProposeClassName(agentId, "Agent"),
            ToolClassName = null,
            Inferred = true
        };
    }
}