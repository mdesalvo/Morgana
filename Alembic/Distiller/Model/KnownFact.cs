namespace Distiller.Model;

/// <summary>
/// One thing known about how the client's work goes, filed under what it is about.
/// </summary>
/// <remarks>
/// The interview's own material: what its questions are built on, never what the agents say. Kept
/// apart by origin because the two are not worth the same. What the client said is settled and is
/// never put to them again; what Alembic read off a configuration somebody else wrote is a reading
/// that may be wrong, and a step holding one owes the client the chance to correct it inside a
/// question it was going to ask anyway.
/// </remarks>
/// <param name="Subject">
/// What it is about, in one or two of the client's own words — 'prices', 'opening hours', 'custom
/// cakes'. It is what a step three passes away reads to decide whether this is worth asking for:
/// the memory of a domain is listed by subject and fetched by subject, so nothing is carried into a
/// turn that has no use for it.
/// </param>
/// <param name="Fact">One short sentence about their work, in their own vocabulary.</param>
/// <param name="Inferred">
/// <c>true</c> where it was read off an uploaded <c>agents.json</c> rather than said by anyone: an
/// upload carries finished prose and no memory of the conversation that produced it.
/// </param>
public sealed record KnownFact(string Subject, string Fact, bool Inferred = false);
