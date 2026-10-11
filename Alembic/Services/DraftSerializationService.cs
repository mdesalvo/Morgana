using System.Text.Json;
using System.Text.Json.Serialization;
using Alembic.Interfaces;
using Alembic.Models;

namespace Alembic.Services;

/// <summary>
/// Writes and reads <c>alembic-draft.json</c>, the save file that holds the whole Draft between sittings.
/// </summary>
public class DraftSerializationService : IDraftSerializationService
{
    /// <summary>The layout of the save file: indented and with enums as words, since the client may read or edit it between sittings.</summary>
    private static readonly JsonSerializerOptions DraftOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Receives the notice that an uploaded save file could not be read.</summary>
    private readonly ILogger logger;

    /// <summary>
    /// Initializes the service over the log that records unreadable save files.
    /// </summary>
    public DraftSerializationService(ILogger logger)
    {
        this.logger = logger;
    }

    /// <inheritdoc />
    /// <remarks>The Draft is written whole (provenance, code facts, baseline and sitting), unlike the projected <c>agents.json</c>.</remarks>
    /// <returns>The whole <c>alembic-draft.json</c>, UTF-8 encoded.</returns>
    public byte[] Serialize(DomainDraft draft) =>
        JsonSerializer.SerializeToUtf8Bytes(draft, DraftOptions);

    /// <inheritdoc />
    /// <param name="draftJson">The uploaded save file, positioned at its start.</param>
    /// <param name="cancellationToken">Stops the read when the client leaves the page.</param>
    /// <returns>The resumed Draft or <c>null</c> when the file is not a Draft, which the caller reports to the client.</returns>
    public async Task<DomainDraft?> DeserializeAsync(Stream draftJson, CancellationToken cancellationToken = default)
    {
        try
        {
            // The upload becomes the Draft the client resumes.
            DomainDraft? draft = await JsonSerializer.DeserializeAsync<DomainDraft>(draftJson, DraftOptions, cancellationToken);

            // The fallback intent belongs to Morgana's classifier: no interview here can write, edit or emit it.
            if (draft?.DropFallbackIntent() == true)
                logger.LogInformation("The resumed sitting declared the '{Fallback}' intent; it has been dropped", DomainDraft.FallbackIntent);

            // The resumed Draft is handed over without the fallback intent.
            return draft;
        }
        catch (JsonException ex)
        {
            // A file that is not a Draft is reported to the client by the caller through the null.
            logger.LogWarning(ex, "Could not read the uploaded alembic-draft.json");
            return null;
        }
    }
}
