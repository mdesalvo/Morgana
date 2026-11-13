using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// States what this Draft changes against the configuration uploaded into it, a greenfield domain included, where everything is new.
/// It is unconditional because a report that only appears when something is wrong is a report nobody has learned to read the day it matters.
/// Alembic never sees the client's C#, so it names every change precisely enough to apply by hand; a changed signature is what stops their half compiling.
/// </summary>
public interface IMigrationReportService
{
    /// <summary>Diffs the Draft against its own baseline.</summary>
    /// <param name="draft">The domain as it stands.</param>
    /// <returns>The report, structured for the page and rendered for the archive.</returns>
    MigrationReport Build(DomainDraft draft);
}

/// <summary>What changed and what has to be done about it.</summary>
/// <param name="BaselineName">The uploaded file this is diffed against; <c>null</c> for greenfield.</param>
/// <param name="Entries">Every change, most consequential first.</param>
/// <param name="Markdown">The same content as <c>MIGRATION.md</c> in the archive.</param>
public sealed record MigrationReport(
    string? BaselineName,
    IReadOnlyList<MigrationEntry> Entries,
    string Markdown);

/// <summary>One change.</summary>
/// <param name="Kind">What kind of thing changed.</param>
/// <param name="Where">Its name, as the configuration addresses it.</param>
/// <param name="Change">What happened to it.</param>
/// <param name="Detail">What the client has to do; otherwise why it does not matter.</param>
public sealed record MigrationEntry(
    MigrationKind Kind,
    string Where,
    MigrationChange Change,
    string Detail);

/// <summary>What kind of element a change is about.</summary>
public enum MigrationKind
{
    /// <summary>An intent.</summary>
    Intent,

    /// <summary>An agent's prose.</summary>
    Agent,

    /// <summary>A tool's description.</summary>
    Tool,

    /// <summary>A tool's parameter list, the one change that breaks compiled code.</summary>
    Signature
}

/// <summary>What happened to an element, ordered by how much work it costs the client, most first, since the report is sorted on it.</summary>
public enum MigrationChange
{
    /// <summary>Gone from the configuration, while its C# may still be referenced.</summary>
    Removed,

    /// <summary>Its shape changed in a way compiled code can feel.</summary>
    SignatureChanged,

    /// <summary>New, its C# not written yet.</summary>
    Added,

    /// <summary>Its prose changed, with nothing to compile.</summary>
    Revised
}