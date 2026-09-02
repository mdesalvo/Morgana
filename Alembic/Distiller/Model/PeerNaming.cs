namespace Distiller.Model;

/// <summary>
/// How a declared colleague is named to a reader — the client, the model conducting the interview,
/// or the migration report.
/// </summary>
/// <remarks>
/// One place, because the same edge is rendered by the interview, the coherence pass, the validator
/// and the report, and a colleague described four ways is four colleagues to whoever reads them
/// side by side.
/// </remarks>
public static class PeerNaming
{
    /// <summary>Names one colleague, saying where it lives only when that is not this domain.</summary>
    /// <param name="peer">The declared colleague.</param>
    public static string Describe(Morgana.AI.Records.PeerReference peer)
        => peer.Partner is null ? peer.Intent : $"{peer.Intent} at {peer.Partner}";

    /// <summary>Names a set of colleagues, or says there are none.</summary>
    /// <param name="peers">The declared colleagues.</param>
    public static string Describe(IEnumerable<Morgana.AI.Records.PeerReference> peers)
    {
        string[] described = [.. peers.Select(Describe)];

        return described.Length > 0 ? string.Join(", ", described) : "none";
    }

    /// <summary>
    /// True when the two name the same colleague: same intent, at the same partner or both at home.
    /// </summary>
    /// <remarks>
    /// Case-insensitive on both halves, the way the framework matches an intent and a partner entry.
    /// The pair and not the intent, because one desk may hold a colleague of the same name at two
    /// installations, which the framework offers as two distinct functions.
    /// </remarks>
    /// <param name="peer">One colleague.</param>
    /// <param name="other">The other.</param>
    public static bool Same(Morgana.AI.Records.PeerReference peer, Morgana.AI.Records.PeerReference other)
        => string.Equals(peer.Intent, other.Intent, StringComparison.OrdinalIgnoreCase)
           && string.Equals(peer.Partner, other.Partner, StringComparison.OrdinalIgnoreCase);
}
