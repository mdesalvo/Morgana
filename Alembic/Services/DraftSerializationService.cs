using System.Text.Json;
using System.Text.Json.Serialization;
using Alembic.Interfaces;
using Alembic.Model;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IDraftSerializationService"/>: System.Text.Json over the Draft model.
/// </summary>
public class DraftSerializationService : IDraftSerializationService
{
    /// <summary>
    /// Indented and enum-as-string because this file is downloaded, read and sometimes hand-edited
    /// by the client between sittings — it is a working document, not a wire payload.
    /// </summary>
    private static readonly JsonSerializerOptions DraftOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Logger for deserialization failures.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the serialization service.
    /// </summary>
    /// <param name="logger">Logger for deserialization failures.</param>
    public DraftSerializationService(ILogger logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Serializes the Draft model directly — every field, including provenance, code facts, the
    /// baseline and the in-progress <see cref="DomainDraft.Sitting"/> — unlike <see cref="DraftExportService"/>,
    /// which projects onto Morgana's own record types. This file is Alembic's alone; nothing else
    /// reads it, so nothing constrains its shape but round-tripping cleanly.
    /// </remarks>
    /// <param name="draft">The Draft to serialize, whole — this is the interview's own save file.</param>
    /// <returns>The whole <c>alembic-draft.json</c>, UTF-8 encoded and indented for a client who opens it by hand.</returns>
    public byte[] Serialize(DomainDraft draft) =>
        JsonSerializer.SerializeToUtf8Bytes(draft, DraftOptions);

    /// <inheritdoc />
    /// <param name="draftJson">The uploaded file's raw bytes, positioned at its start.</param>
    /// <param name="cancellationToken">Cancels the deserialization.</param>
    /// <returns>The resumed Draft, or <c>null</c> if the file could not be parsed as one — the
    /// caller falls back to treating the upload as a bare configuration in that case.</returns>
    public async Task<DomainDraft?> DeserializeAsync(Stream draftJson, CancellationToken cancellationToken = default)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<DomainDraft>(draftJson, DraftOptions, cancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Could not read the uploaded alembic-draft.json");
            return null;
        }
    }
}
