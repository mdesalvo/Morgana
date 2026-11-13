namespace Alembic.Models;

/// <summary>
/// What reading an uploaded domain yielded, reported rather than thrown.
/// A client whose upload could not be read still has a domain to work on and loses only the head start.
/// </summary>
/// <param name="Error">Why nothing was written; <c>null</c> when the reading was filed onto the Draft.</param>
public sealed record DomainReading(string? Error = null);