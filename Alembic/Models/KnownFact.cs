namespace Alembic.Models;

/// <summary>
/// One thing known about how the client's work goes, filed under what it is about.
/// </summary>
/// <remarks>A fact the client stated is settled while one Alembic inferred from an upload may be wrong and is offered back for correction.</remarks>
/// <param name="Subject">The one or two words of the client's own ('prices', 'custom cakes') by which a later step lists and fetches the fact.</param>
/// <param name="Fact">One short sentence about their work in their own vocabulary.</param>
/// <param name="Inferred"><c>true</c> where Alembic read the fact off an uploaded configuration instead of the client saying it.</param>
public sealed record KnownFact(string Subject, string Fact, bool Inferred = false);
