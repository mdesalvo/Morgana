namespace Alembic.Models;

/// <summary>
/// What reading an uploaded domain yielded, reported rather than thrown.
/// A client whose upload could not be read still has a domain to work on and loses only the head start.
/// </summary>
/// <param name="Written">How many facts about the client's work were written onto the Draft.</param>
/// <param name="Error">Why nothing was written, where nothing was.</param>
public sealed record DomainReading(int Written, string? Error = null);
