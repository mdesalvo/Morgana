using System.Text.Json;
using System.Text.Json.Serialization;
using Alembic.Interfaces;
using Alembic.Models;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IDraftSerializationService"/>: the Draft model itself as JSON, every field included.
/// Unlike the export it projects onto nothing, since no reader but Alembic opens this file and only a clean round trip constrains its shape.
/// </summary>
public class DraftSerializationService : IDraftSerializationService
{
    /// <summary>Indented with enums as names, because the client downloads this file and sometimes edits it by hand between sittings.</summary>
    private static readonly JsonSerializerOptions DraftOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Records an upload that is not a save file.</summary>
    private readonly ILogger logger;

    /// <summary>Binds the service to its logger.</summary>
    /// <param name="logger">Records an upload that is not a save file.</param>
    public DraftSerializationService(ILogger logger)
    {
        this.logger = logger;
    }

    /// <summary>Renders the whole Draft, provenance, code facts, baseline and sitting included, as the interview's save file.</summary>
    /// <param name="draft">The Draft to serialize, whole.</param>
    /// <returns>The whole <c>alembic-draft.json</c>, UTF-8 encoded and indented for a client who opens it by hand.</returns>
    public byte[] Serialize(DomainDraft draft) =>
        JsonSerializer.SerializeToUtf8Bytes(draft, DraftOptions);

    /// <summary>Reads back a Draft the client saved earlier; <c>null</c> when the file does not parse as one.</summary>
    /// <param name="draftJson">The uploaded file's raw bytes, positioned at its start.</param>
    /// <param name="cancellationToken">Stops reading the stream.</param>
    /// <returns>The resumed Draft; <c>null</c> when the upload is not one, which the caller then reads as a bare configuration.</returns>
    public async Task<DomainDraft?> DeserializeAsync(Stream draftJson, CancellationToken cancellationToken = default)
    {
        try
        {
            // The save file is the Draft itself, read back field for field.
            return await JsonSerializer.DeserializeAsync<DomainDraft>(draftJson, DraftOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            // Not a save file: the upload is tried as an agents.json next, so this is a fork in the road rather than a failure.
            logger.LogWarning(ex, "Could not read the uploaded alembic-draft.json");
            return null;
        }
    }
}