namespace Alembic.Models;

/// <summary>
/// How a declared colleague is named to the client, the interview model and the migration report.
/// </summary>
/// <remarks>The interview, the coherence pass, the validator and the report share these names so one colleague reads as one.</remarks>
public static class PeerNaming
{
    /// <summary>Names one colleague, saying where it lives only when that is not this domain.</summary>
    public static string Describe(Morgana.AI.Records.PeerReference peer)
        => peer.Instance is null ? peer.Intent : $"{peer.Intent} at {peer.Instance}";

    /// <summary>Names a set of colleagues, or says there are none.</summary>
    public static string Describe(IEnumerable<Morgana.AI.Records.PeerReference> peers)
    {
        // Each colleague is named once and in the order declared.
        string[] descriptions = [.. peers.Select(Describe)];

        // An empty set reads as the word "none" so a prompt never carries a bare blank.
        return descriptions.Length > 0 ? string.Join(", ", descriptions) : "none";
    }

    /// <summary>
    /// True when the two name the same colleague: same intent, at the same instance or both at home.
    /// </summary>
    /// <remarks>Intent and instance are compared together, since one agent may hold a colleague of the same name at two installations.</remarks>
    public static bool Same(Morgana.AI.Records.PeerReference peer, Morgana.AI.Records.PeerReference other)
        // Both halves match without regard to case, as the framework matches an intent and an instance entry.
        => string.Equals(peer.Intent, other.Intent, StringComparison.OrdinalIgnoreCase)
           && string.Equals(peer.Instance, other.Instance, StringComparison.OrdinalIgnoreCase);
}
