namespace Alembic.Models;

/// <summary>
/// One thing known about how the client's work goes, filed under what it is about: what the questions are built on, never what the agents say.
/// What the client said is settled; what Alembic read off an upload may be wrong, so a step holding one lets the client correct it.
/// </summary>
/// <param name="Subject">What it is about, in one or two of the client's own words, by which another agent's step decides whether to fetch it.</param>
/// <param name="Fact">One short sentence about their work, in their own vocabulary.</param>
/// <param name="Inferred"><c>true</c> where it was read off an uploaded <c>agents.json</c> rather than said by anyone.</param>
public sealed record KnownFact(string Subject, string Fact, bool Inferred = false);