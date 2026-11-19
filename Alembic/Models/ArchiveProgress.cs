namespace Alembic.Models;

/// <summary>
/// What happened to one file of the archive while it was being built.
/// </summary>
public enum ArchiveProgressKind
{
    /// <summary>A file landed in the archive.</summary>
    Written,

    /// <summary>A mock is being read by the CodeGuard.</summary>
    Checking,

    /// <summary>The CodeGuard refused a mock once and it is being written again.</summary>
    Rewriting,

    /// <summary>The CodeGuard refused a mock twice and it stays out of the archive.</summary>
    Refused
}

/// <summary>
/// One event of the build, reported to the page that draws the distillation.
/// </summary>
/// <param name="Path">The file the event is about, as the archive names it.</param>
/// <param name="Kind">What happened to that file.</param>
/// <param name="Violation">The CodeGuard's reason for refusing the mock; <c>null</c> for an event that is not a refusal.</param>
public sealed record ArchiveProgress(string Path, ArchiveProgressKind Kind, string? Violation = null);