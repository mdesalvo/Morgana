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
            Agents = [.. configuration.Agents.Select(agent => ToAgentDraft(agent, notices))]
        };

        notices.Insert(0, $"Read {draft.Intents.Count} intents and {draft.Agents.Count} agents, "
                          + $"carrying {draft.Agents.Sum(a => a.Tools.Count)} tools.");

        logger.LogInformation(
            "Imported {IntentCount} intents and {AgentCount} agents from {FileName}",
            draft.Intents.Count, draft.Agents.Count, fileName);

        return new DraftImportResult(draft, notices, null);
    }

    /// <summary>
    /// Projects an intent definition onto its Draft element.
    /// </summary>
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
