using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Alembic.Interfaces;
using Alembic.Model;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IDraftExportService"/>: serializes the Draft through
/// <see cref="DraftProjection"/>, which rebuilds Morgana's own record types.
/// </summary>
/// <remarks>
/// The output shape is not hand-written JSON: it is <c>Records.IntentDefinition</c> and
/// <c>Records.Prompt</c> serialized as they are, so the file Alembic emits and the file Morgana
/// reads are the same type seen from two sides. A field added to those records reaches this
/// exporter without anyone remembering to come here.
/// <para>
/// The projection is shared with the recap on purpose: a recap composed from a slightly different
/// prompt than the one that gets written would be a recap of a domain nobody is going to run.
/// </para>
/// </remarks>
public class DraftExportService : IDraftExportService
{
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
    /// <param name="draft">The domain to serialize — every intent and agent, projected through
    /// <see cref="DraftProjection"/> into the framework's own record types before serialization.</param>
    /// <returns>The whole <c>agents.json</c>, UTF-8 encoded, indented, ready to write to disk or to an archive entry as is.</returns>
    public byte[] Export(DomainDraft draft)
    {
        AgentsConfigurationFile file = new AgentsConfigurationFile(
            [.. draft.Intents.Select(DraftProjection.ToIntentDefinition)],
            [.. draft.Agents.Select(DraftProjection.ToPrompt)]);

        return JsonSerializer.SerializeToUtf8Bytes(file, ExportOptions);
    }
}
