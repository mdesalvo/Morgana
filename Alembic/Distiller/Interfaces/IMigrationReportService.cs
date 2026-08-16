using Distiller.Model;

namespace Distiller.Interfaces;

/// <summary>
/// States what this Draft changes against the configuration that was uploaded into it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unconditional.</b> It is produced for a greenfield domain too, where it says everything is
/// new — because a report that only appears when something is wrong is a report nobody learns to
/// read, and this one has to be read on the day it finally matters.
/// </para>
/// <para>
/// It exists because Alembic never sees the client's tree. It cannot know which C# is already
/// there, cannot merge, and must not pretend to: what it can do is name every change precisely
/// enough that a human applies it in a minute. The signature section is the load-bearing one — a
/// tool whose parameters changed still compiles on the generated side and fails at Morgana's
/// startup in <c>MorganaToolAdapter.AddTool</c>, and the client-owned half is exactly where that
/// fix has to be made by hand.
/// </para>
/// </remarks>
public interface IMigrationReportService
{
    /// <summary>
    /// Diffs the Draft against its own baseline.
    /// </summary>
    /// <param name="draft">The domain as it stands.</param>
    /// <returns>The report, structured for the page and rendered for the archive.</returns>
    MigrationReport Build(DomainDraft draft);
}

/// <summary>
/// What changed, and what has to be done about it.
/// </summary>
/// <param name="BaselineName">The uploaded file this is diffed against, or <c>null</c> for greenfield.</param>
/// <param name="Entries">Every change, most consequential first.</param>
/// <param name="Markdown">The same content as <c>MIGRATION.md</c> in the archive.</param>
public sealed record MigrationReport(
    string? BaselineName,
    IReadOnlyList<MigrationEntry> Entries,
    string Markdown);

/// <summary>
/// One change.
/// </summary>
/// <param name="Kind">What kind of thing changed.</param>
/// <param name="Where">Its name, as the configuration addresses it.</param>
/// <param name="Change">What happened to it.</param>
/// <param name="Detail">What the client has to do, or why it does not matter.</param>
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

    /// <summary>A tool's parameter list — the one that breaks compiled code.</summary>
    Signature
}

/// <summary>What happened to an element.</summary>
/// <remarks>
/// Ordered by how much work it costs the client, most first, because the report is sorted on it.
/// </remarks>
public enum MigrationChange
{
    /// <summary>Gone from the configuration. Its C# is now dead, and may still be referenced.</summary>
    Removed,

    /// <summary>Its shape changed in a way compiled code can feel.</summary>
    SignatureChanged,

    /// <summary>New. Its C# does not exist yet.</summary>
    Added,

    /// <summary>Its prose changed. Nothing to compile.</summary>
    Revised
}
