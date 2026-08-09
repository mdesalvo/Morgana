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
    public byte[] Serialize(DomainDraft draft) =>
        JsonSerializer.SerializeToUtf8Bytes(draft, DraftOptions);

    /// <inheritdoc />
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
