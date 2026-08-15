using System.Text.Json;
using Alembic.Interfaces;
using Alembic.Model;
using Morgana.AI;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IDraftImportService"/>: parses an uploaded <c>agents.json</c> with Morgana's
/// own record types and projects it onto a <see cref="DomainDraft"/>.
/// </summary>
/// <remarks>
/// The projection is deliberately lossless in both directions. Every field the framework models is
/// mapped, and every AdditionalProperties key it does not is carried through verbatim in
/// <see cref="AgentDraft.UnmodelledProperties"/> — because the round-trip invariant (import then
/// export returns an equivalent file) must not depend on Alembic having a use for what it reads.
/// </remarks>
public class DraftImportService : IDraftImportService
{
    /// <summary>
    /// The AdditionalProperties key carrying an agent's toolkit. Compared ordinally, on purpose:
    /// <c>Records.Prompt.GetAdditionalProperty</c> looks it up in a plain
    /// <c>Dictionary&lt;string, object&gt;</c>, so a differently-cased key is invisible to the
    /// framework — and must therefore stay invisible here too, rather than being silently
    /// promoted into a toolkit Morgana would never load.
    /// </summary>
    private const string ToolsPropertyName = "Tools";

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
    /// does), and a zip archive is unwrapped by the caller before either service sees it.
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
            // Frozen here, before anything can touch it, and by a second projection of the same parsed
            // configuration rather than a copy of what was just built: the baseline is the file, and
            // deriving it the same way guarantees the migration report can only ever show a real edit.
            Baseline = new DomainDraft
            {
                ImportedFrom = fileName,
                Intents = [.. configuration.Intents.Select(ToIntentDraft)],
                Agents = [.. configuration.Agents.Select(agent => ToAgentDraft(agent, []))]
            }
        };

        // On the working draft only. The baseline is the file as it arrived, so a domain that came
        // without a fallback shows the addition in the migration report, which is where the client
        // should meet it — not silently, and not as something they have to write themselves.
        int before = draft.Intents.Count;
        draft.EnsureFallbackIntent();

        if (draft.Intents.Count > before)
            notices.Add($"This configuration had no '{DomainDraft.FallbackIntent}' intent, so one is in place: "
                        + "it is where the classifier sends everything the domain does not cover, and it is the one intent no agent may claim.");

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
    /// Projects an agent prompt onto its Draft element, splitting the toolkit out of
    /// AdditionalProperties and keeping whatever else was in there untouched.
    /// </summary>
    /// <param name="prompt">One entry of the uploaded configuration's <c>Agents</c> array, in the
    /// framework's own record shape.</param>
    /// <param name="notices">Import notices are appended here for keys Alembic does not model —
    /// pass an empty list to suppress them, as the baseline projection below does, since the same
    /// unmodelled keys would otherwise be reported twice for one upload.</param>
    private AgentDraft ToAgentDraft(Records.Prompt prompt, List<string> notices)
    {
        List<Records.ToolDefinition> tools =
            prompt.GetAdditionalPropertyOrDefault<List<Records.ToolDefinition>>(ToolsPropertyName, []);

        // Everything in AdditionalProperties other than the toolkit, kept as read. Entries that
        // held nothing but Tools disappear rather than being written back empty.
        List<Dictionary<string, object>> unmodelled =
            [.. prompt.AdditionalProperties
                      .Select(entry => entry.Where(pair => pair.Key != ToolsPropertyName)
                                            .ToDictionary(pair => pair.Key, pair => pair.Value))
                      .Where(entry => entry.Count > 0)];

        foreach (string key in unmodelled.SelectMany(entry => entry.Keys))
            notices.Add($"Agent '{prompt.ID}' declares '{key}', which Alembic does not model. "
                        + "It will be written back exactly as it was read.");

        return new AgentDraft
        {
            ID = prompt.ID,
            Type = prompt.Type,
            SubType = prompt.SubType,
            Target = prompt.Target,
            Instructions = prompt.Instructions,
            Formatting = prompt.Formatting,
            Personality = prompt.Personality,
            Language = prompt.Language,
            Version = prompt.Version,
            Tools = [.. tools.Select(ToToolDraft)],
            UnmodelledProperties = unmodelled,
            Code = InferCodeFacts(prompt.ID, tools.Count > 0),
            Origin = Provenance.Imported
        };
    }

    /// <summary>
    /// Projects a tool definition onto its Draft element.
    /// </summary>
    /// <param name="tool">One tool of an agent's toolkit, in the framework's own record shape.</param>
    private static ToolDraft ToToolDraft(Records.ToolDefinition tool) => new()
    {
        Name = tool.Name,
        Description = tool.Description,
        Parameters = [.. tool.Parameters.Select(parameter => new ToolParameterDraft
        {
            Name = parameter.Name,
            Description = parameter.Description,
            Required = parameter.Required,
            Scope = string.IsNullOrWhiteSpace(parameter.Scope) ? null : parameter.Scope,
            Shared = parameter.Shared
        })],
        Origin = Provenance.Imported
    };

    /// <summary>
    /// Proposes the C# facts an <c>agents.json</c> cannot carry.
    /// </summary>
    /// <remarks>
    /// Only the class names are proposed, from the framework's own naming convention. Namespace and
    /// tier are left null rather than guessed: they are not derivable from anything in the file, and
    /// a confident-looking wrong value is worse than an empty one the interview will ask about.
    /// Everything here is flagged <see cref="AgentCodeFacts.Inferred"/> — a proposal for the client
    /// to confirm, never a finding.
    /// </remarks>
    /// <param name="agentId">The intent name this agent answers — its <c>Prompt.ID</c>, the only thing
    /// the naming convention has to go on.</param>
    /// <param name="hasTools">Whether the agent's toolkit is non-empty; a tool class is only proposed
    /// when there would be one to hold.</param>
    private static AgentCodeFacts InferCodeFacts(string agentId, bool hasTools)
    {
        string bareName = agentId.Trim();

        return new AgentCodeFacts
        {
            AgentClassName = bareName.Length > 0 ? $"{char.ToUpperInvariant(bareName[0])}{bareName[1..]}Agent" : null,
            ToolClassName = hasTools && bareName.Length > 0 ? $"{char.ToUpperInvariant(bareName[0])}{bareName[1..]}Tool" : null,
            Inferred = true
        };
    }
}
