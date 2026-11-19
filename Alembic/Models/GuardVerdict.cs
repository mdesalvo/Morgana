namespace Alembic.Models;

/// <summary>
/// What a guard decided about a text and, when it refused, the sentence the client reads.
/// </summary>
/// <param name="IsCompliant">Whether the text may go on to the model or the archive.</param>
/// <param name="Violation">What the client reads when the text was refused; <c>null</c> when it passed.</param>
public sealed record GuardVerdict(bool IsCompliant, string? Violation);