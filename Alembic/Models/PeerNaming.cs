namespace Alembic.Models;

/// <summary>
/// How a declared colleague is named to a reader: the client, the model conducting the interview or the migration report.
/// One place, since a colleague described four ways by four screens is four colleagues to whoever reads them side by side.
/// </summary>
public static class PeerNaming
{
    /// <summary>Names one colleague, saying which partner publishes it only when that is not this domain.</summary>
    /// <param name="peer">The declared colleague.</param>
    public static string Describe(Morgana.AI.Records.PeerReference peer)
        => peer.Instance is null ? peer.Intent : $"{peer.Intent} at {peer.Instance}";

    /// <summary>Names a set of colleagues, or says there are none.</summary>
    /// <param name="peers">The declared colleagues.</param>
    public static string Describe(IEnumerable<Morgana.AI.Records.PeerReference> peers)
    {
        string[] colleagueNames = [.. peers.Select(Describe)];

        // An agent without colleagues says so in a word, rather than leaving an empty line a reader takes for a fault.
        return colleagueNames.Length > 0 ? string.Join(", ", colleagueNames) : "none";
    }

    /// <summary>
    /// True when the two name the same colleague: same intent, at the same partner or both at home, ignoring case as the framework does.
    /// The pair and not the intent, since one agent may hold colleagues of the same intent at two partners, offered as two functions.
    /// </summary>
    /// <param name="peer">One colleague.</param>
    /// <param name="other">The other.</param>
    public static bool Same(Morgana.AI.Records.PeerReference peer, Morgana.AI.Records.PeerReference other)
        => string.Equals(peer.Intent, other.Intent, StringComparison.OrdinalIgnoreCase)
           && string.Equals(peer.Instance, other.Instance, StringComparison.OrdinalIgnoreCase);
}
