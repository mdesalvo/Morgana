using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Models;
using Morgana.AI;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IDraftImportService"/>: parses an uploaded <c>agents.json</c> with Morgana's
/// own record types and projects it onto a <see cref="DomainDraft"/>.
/// </summary>
/// <remarks>
/// The import reads the agents' prose and carries every AdditionalProperties key through verbatim in <see cref="AgentDraft.UnmodelledProperties"/>; the toolkit is authored by the interview because the tool contracts live in the client's C#, which Alembic never sees.
/// </remarks>
public class DraftImportService : IDraftImportService
{
    /// <summary>
    /// Case-insensitive, mirroring how the framework itself reads <c>agents.json</c>: an author
    /// whose file loads in Morgana must find it loads here.
    /// </summary>
    private static readonly JsonSerializerOptions ImportOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Logger for import diagnostics.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the import service.
    /// </summary>
    /// <param name="logger">Logger for import diagnostics.</param>
    public DraftImportService(ILogger logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// This method only ever handles the configuration shape — a bare <c>agents.json</c>. Telling
    /// it apart from a resumed interview's own save file is the caller's job (by content, not by
    /// file name: a serialized Draft carries <c>CreatedAt</c> at the root and a configuration never
    /// does) and a zip archive is unwrapped by the caller before either service sees it.
    /// </remarks>
    /// <param name="agentsJson">The uploaded file's raw bytes, positioned at its start.</param>
    /// <param name="fileName">The name the client uploaded it under, kept only for the Draft's own
    /// <see cref="DomainDraft.ImportedFrom"/> and for log messages — never parsed for meaning.</param>
    /// <param name="cancellationToken">Cancels the deserialization.</param>
    /// <returns>The parsed Draft plus any notices worth surfacing, or a Draft-less result carrying
    /// one explanatory error when the file could not be read at all.</returns>
    public async Task<DraftImportResult> ImportAsync(
        Stream agentsJson,
        string fileName,
        CancellationToken cancellationToken = default)
    {
        AgentsConfigurationFile? configuration;

        try
        {
            configuration = await JsonSerializer.DeserializeAsync<AgentsConfigurationFile>(
                agentsJson, ImportOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not parse the uploaded agents.json ({FileName})", fileName);
            return new DraftImportResult(null, [], $"This is not a readable agents.json: {ex.Message}");
        }

        if (configuration is null)
            return new DraftImportResult(null, [], "The uploaded file is empty.");

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

        return new DraftImportResult(draft, notices, null);
    }

    /// <summary>
    /// Projects an intent definition onto its Draft element.
    /// </summary>
    /// <param name="intent">One entry of the uploaded configuration's <c>Intents</c> array.</param>
    private static IntentDraft ToIntentDraft(Records.IntentDefinition intent) => new()
    {
        Name = intent.Name,
        Description = intent.Description,
        Label = intent.Label,
        DefaultValue = intent.DefaultValue,
        Origin = Provenance.Imported
    };

    /// <summary>
    /// Projects an agent prompt onto its Draft element, keeping every AdditionalProperties entry untouched.
    /// </summary>
    /// <param name="prompt">One entry of the uploaded configuration's <c>Agents</c> array, in the
    /// framework's own record shape.</param>
    /// <param name="notices">Import notices are appended here for keys Alembic does not model —
    /// pass an empty list to suppress them, as the baseline projection below does, since the same
    /// unmodelled keys would otherwise be reported twice for one upload.</param>
    private AgentDraft ToAgentDraft(Records.Prompt prompt, List<string> notices)
    {
        // Every key is carried as read, since the toolkit no longer lives here. Entries that held
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
    /// Proposes the C# facts an <c>agents.json</c> cannot carry.
    /// </summary>
    /// <remarks>
    /// Only the agent class name is proposed, from the framework's own naming convention; the tool class falls back to it at emit. Namespace and
    /// tier are left null rather than guessed: they are not derivable from anything in the file and
    /// a confident-looking wrong value is worse than an empty one the interview will ask about.
    /// Everything here is flagged <see cref="AgentCodeFacts.Inferred"/> — a proposal for the client
    /// to confirm, never a finding.
    /// </remarks>
    /// <param name="agentId">The intent name this agent answers — its <c>Prompt.ID</c>, the only thing
    /// the naming convention has to go on.</param>
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
