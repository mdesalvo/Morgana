using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alembic.Interfaces;
using Alembic.Models;

namespace Alembic.Services;

/// <summary>
/// Writes the Draft as the <c>agents.json</c> that Morgana reads, through the projection that the recap also uses.
/// </summary>
/// <remarks>The file is Morgana's own intent and prompt records serialized as they are, so the recap and the file describe the same domain.</remarks>
public class DraftExportService : IDraftExportService
{
    /// <summary>The layout of the exported file: indented for a client who reads and commits it, with accents written literally.</summary>
    /// <remarks>Characters outside the BMP such as emoji still come out as escaped surrogate pairs.</remarks>
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <inheritdoc />
    /// <returns>The whole <c>agents.json</c>, UTF-8 encoded, ready to write into the archive as it is.</returns>
    public byte[] Export(DomainDraft draft)
    {
        // The file holds every intent and every agent, each projected into the record Morgana reads back.
        AgentsConfigurationFile file = new AgentsConfigurationFile(
            [.. draft.Intents.Select(DraftProjection.ToIntentDefinition)],
            [.. draft.Agents.Select(DraftProjection.ToPrompt)]);

        // The archive entry receives the bytes of the file.
        return JsonSerializer.SerializeToUtf8Bytes(file, ExportOptions);
    }
}
