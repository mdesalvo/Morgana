using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alembic.Interfaces;
using Alembic.Models;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IDraftExportService"/>: serializes the Draft through <see cref="DraftProjection"/>, which rebuilds Morgana's own record types.
/// The file Alembic writes and the file Morgana reads are the same records seen from two sides, so a field added to them reaches this export unasked;
/// the projection is the recap's too, so what the client reviewed is what gets written.
/// </summary>
public class DraftExportService : IDraftExportService
{
    /// <summary>
    /// Indented, null-free and lightly escaped, because the client reads, reviews and commits this file.
    /// An unstated Personality stays out rather than written as null, while false booleans are written since Morgana reads both alike and stating it is clearer.
    /// The relaxed encoder keeps accents, dashes and apostrophes literal, safe because the file is downloaded and never placed in markup;
    /// an emoji lies outside its ranges and still escapes, so the first export normalises a hand-written file once and every later one diffs cleanly.
    /// </summary>
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Renders the Draft as the <c>agents.json</c> the client downloads.</summary>
    /// <param name="draft">The domain to serialize, every intent and agent projected into the framework's own record types.</param>
    /// <returns>The whole <c>agents.json</c>, UTF-8 encoded and indented, ready to write to an archive entry as is.</returns>
    public byte[] Export(DomainDraft draft)
    {
        // The file Morgana loads: its intents and its agents' prompts, in the Draft's own order.
        AgentsConfigurationFile file = new AgentsConfigurationFile(
            [.. draft.Intents.Select(DraftProjection.ToIntentDefinition)],
            [.. draft.Agents.Select(DraftProjection.ToPrompt)]);

        // Serialized whole, in the shape a client commits.
        return JsonSerializer.SerializeToUtf8Bytes(file, ExportOptions);
    }
}