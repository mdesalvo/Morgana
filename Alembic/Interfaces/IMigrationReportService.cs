using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// States what this Draft changes against the archive that was last emitted from it.
/// </summary>
/// <remarks>The report is produced for a greenfield domain too, where it says everything is new.</remarks>
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
/// What changed and what has to be done about it.
/// </summary>
/// <param name="BaselineName">What this is diffed against: the archive last emitted or an imported file; <c>null</c> for greenfield.</param>
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
    Signature,

    /// <summary>A workflow of an agent, which lives in agents.json alone.</summary>
    Workflow
}

/// <summary>What happened to an element.</summary>
/// <remarks>The members run from the change that costs the client most work to the one that costs least, which is the order of the report.</remarks>
public enum MigrationChange
{
    /// <summary>Gone from the configuration. Its C# is now dead and may still be referenced.</summary>
    Removed,

    /// <summary>Its shape changed in a way compiled code can feel.</summary>
    SignatureChanged,

    /// <summary>New. Its C# does not exist yet.</summary>
    Added,

    /// <summary>Its prose changed. Nothing to compile.</summary>
    Revised
}
