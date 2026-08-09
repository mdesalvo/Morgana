using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alembic.Interfaces;
using Alembic.Model;
using Morgana.AI;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IDraftExportService"/>: rebuilds Morgana's own record types from the Draft
/// and serializes them.
/// </summary>
/// <remarks>
/// The output shape is not hand-written JSON: it is <see cref="Records.IntentDefinition"/> and
/// <see cref="Records.Prompt"/> serialized as they are, so the file Alembic emits and the file
/// Morgana reads are the same type seen from two sides. A field added to those records reaches this
/// exporter without anyone remembering to come here.
/// </remarks>
public class DraftExportService : IDraftExportService
{
    /// <summary>
    /// The AdditionalProperties key carrying an agent's toolkit — the same ordinal name the
    /// importer split out.
    /// </summary>
    private const string ToolsPropertyName = "Tools";

    /// <summary>
    /// Indented because a client reads, reviews and commits this file.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/> keeps an unstated Personality out of the
    /// file rather than writing <c>null</c> into it, while leaving <c>false</c> booleans written
    /// explicitly: an omitted <c>Shared</c> and an explicit <c>"Shared": false</c> mean the same
    /// thing to Morgana, and stating it is the clearer of the two.
    /// <para>
    /// The relaxed encoder is chosen against System.Text.Json's default, which escapes every
    /// non-ASCII character and the apostrophe into <c>\u</c> sequences. A domain configuration is
    /// prose the client reads, reviews and commits, and escaping all of it would leave the export
    /// semantically identical but textually unrecognisable next to the file it came from. It is
    /// safe here because this output is downloaded as a file, never interpolated into markup.
    /// </para>
    /// <para>
    /// It does not get all the way there: the encoder's allow-list is expressed in
    /// <c>UnicodeRange</c>s, which stop at U+FFFF, so a character outside the BMP — every emoji in
    /// a quick-reply label — is still written as an escaped surrogate pair. Accented text, dashes
    /// and apostrophes come through literally; emoji do not. The first export therefore normalises
    /// a hand-written configuration once, and every export after that is diffable against the one
    /// before it, which is the comparison that actually matters in use.
    /// </para>
    /// </remarks>
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <inheritdoc />
    public byte[] Export(DomainDraft draft)
    {
        AgentsConfigurationFile file = new AgentsConfigurationFile(
            [.. draft.Intents.Select(ToIntentDefinition)],
            [.. draft.Agents.Select(ToPrompt)]);

        return JsonSerializer.SerializeToUtf8Bytes(file, ExportOptions);
    }

    /// <summary>
    /// Rebuilds an intent definition from its Draft element.
    /// </summary>
    private static Records.IntentDefinition ToIntentDefinition(IntentDraft intent) =>
        new(intent.Name ?? string.Empty,
            intent.Description ?? string.Empty,
            intent.Label,
            intent.DefaultValue);

    /// <summary>
    /// Rebuilds an agent prompt from its Draft element, putting the toolkit back into
    /// AdditionalProperties alongside whatever else was carried through.
    /// </summary>
    /// <remarks>
    /// The toolkit is written as its own entry, first, and the unmodelled entries follow. This does
    /// not necessarily reproduce the grouping the file arrived with — AdditionalProperties is a list
    /// of dictionaries and the same content can be spread across it in several ways — which is
    /// precisely why the round-trip invariant is stated as equivalence and not as byte identity.
    /// Morgana looks keys up across every entry, so the grouping is not information.
    /// </remarks>
    private static Records.Prompt ToPrompt(AgentDraft agent)
    {
        List<Dictionary<string, object>> additionalProperties = [];

        if (agent.Tools.Count > 0)
            additionalProperties.Add(new Dictionary<string, object>
            {
                [ToolsPropertyName] = agent.Tools.Select(ToToolDefinition).ToList()
            });

        additionalProperties.AddRange(agent.UnmodelledProperties);

        return new Records.Prompt(
            agent.ID ?? string.Empty,
            agent.Type,
            agent.SubType,
            agent.Target ?? string.Empty,
            agent.Instructions ?? string.Empty,
            agent.Formatting ?? string.Empty,
            agent.Personality,
            agent.Language,
            agent.Version,
            additionalProperties);
    }

    /// <summary>
    /// Rebuilds a tool definition from its Draft element.
    /// </summary>
    private static Records.ToolDefinition ToToolDefinition(ToolDraft tool) =>
        new(tool.Name ?? string.Empty,
            tool.Description ?? string.Empty,
            [.. tool.Parameters.Select(parameter => new Records.ToolParameter(
                parameter.Name ?? string.Empty,
                parameter.Description ?? string.Empty,
                parameter.Required,
                // A parameter carrying a value the model itself authors declares no scope. The
                // framework's record types it as a non-nullable string, so "no scope" travels as
                // the empty string — which is what the importer read it back from.
                parameter.Scope ?? string.Empty,
                parameter.Shared))]);
}
