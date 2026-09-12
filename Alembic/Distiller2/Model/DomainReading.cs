namespace Distiller2.Model;

/// <summary>
/// What reading an uploaded domain yielded.
/// </summary>
/// <remarks>
/// Reported rather than thrown: a client whose upload could not be read still has a domain to work
/// on and loses only the head start, so the page says so in a line and carries on.
/// </remarks>
/// <param name="Written">How many facts about the client's work were written onto the Draft.</param>
/// <param name="Error">Why nothing was written, where nothing was.</param>
public sealed record DomainReading(int Written, string? Error = null);
