namespace Distiller2.Model;

/// <summary>
/// A place on the shell's track: the map, one moment of one agent or one moment of the domain.
/// </summary>
/// <param name="Entry">The map entry an agent's moment belongs to; -1 for the map and the domain.</param>
/// <param name="Rail">The moment: 0 the map, 1-7 an agent's, 8-10 the domain's.</param>
public readonly record struct ShellPoint(int Entry, int Rail) : IComparable<ShellPoint>
{
    /// <summary>The map's moment.</summary>
    public const int MapRail = 0;

    /// <summary>The agent's last moment, where the client lets it into the domain.</summary>
    public const int AcceptanceRail = 7;

    /// <summary>The domain's first moment.</summary>
    public const int CollaborationRail = 8;

    /// <summary>
    /// The seven moments every agent goes through, as the track names them.
    /// </summary>
    public static IReadOnlyList<string> AgentMoments { get; } =
        ["Target", "Personality", "Toolkit", "Territory", "Instructions", "Formatting", "Acceptance"];

    /// <summary>
    /// The three moments that close the domain, as the track names them.
    /// </summary>
    public static IReadOnlyList<string> DomainMoments { get; } = ["Collaboration", "Validation", "Archive"];

    /// <summary>
    /// The map, where every interview starts.
    /// </summary>
    public static ShellPoint Map => new(-1, MapRail);

    /// <summary>
    /// Whether this is the map.
    /// </summary>
    public bool IsMap => Rail == MapRail;

    /// <summary>
    /// Whether this is one of an agent's moments.
    /// </summary>
    public bool OnAgent => Entry >= 0 && Rail is > MapRail and <= AcceptanceRail;

    /// <summary>
    /// Whether this is one of the domain's closing moments.
    /// </summary>
    public bool OnDomain => Rail >= CollaborationRail;

    /// <summary>
    /// The moment's name on the track.
    /// </summary>
    public string Name =>
        IsMap ? "Map" : OnAgent ? AgentMoments[Rail - 1] : DomainMoments[Rail - CollaborationRail];

    /// <summary>
    /// Where the state machine stands: the frontier the pointer may never pass.
    /// </summary>
    // The agent passes are numbered 1-6 in the same order as the track's sections, so the pass is the
    // rail. Acceptance is no pass: the interview waits there once Formatting is settled.
    public static ShellPoint FrontierOf(InterviewState interview) => interview switch
    {
        { Pass: InterviewStep.DomainMapper } => Map,
        { Pass: InterviewStep.DomainColleagues } => new ShellPoint(-1, CollaborationRail),
        { Pass: InterviewStep.AgentFormatting, ReadyForReview: true } => new ShellPoint(interview.At, AcceptanceRail),
        _ => new ShellPoint(interview.At, (int)interview.Pass)
    };

    /// <inheritdoc />
    public int CompareTo(ShellPoint other) => Order.CompareTo(other.Order);

    /// <summary>
    /// The track read left to right: the map, every agent in map order, then the domain.
    /// </summary>
    private (int Band, int Entry, int Rail) Order =>
        IsMap ? (0, 0, 0) : OnAgent ? (1, Entry, Rail) : (2, 0, Rail);

    public static bool operator <(ShellPoint left, ShellPoint right) => left.CompareTo(right) < 0;
    public static bool operator >(ShellPoint left, ShellPoint right) => left.CompareTo(right) > 0;
    public static bool operator <=(ShellPoint left, ShellPoint right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ShellPoint left, ShellPoint right) => left.CompareTo(right) >= 0;
}
