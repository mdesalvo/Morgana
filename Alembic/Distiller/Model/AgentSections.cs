namespace Distiller.Model;

/// <summary>
/// An agent's six sections as the shell shows them, keyed by the rail that writes each.
/// </summary>
public static class AgentSections
{
    /// <summary>
    /// The rail whose content is the tools rather than prose.
    /// </summary>
    public const int ToolkitRail = 3;

    /// <summary>
    /// Every section rail, in the order the track walks them.
    /// </summary>
    public static IReadOnlyList<int> All { get; } = [1, 2, 3, 4, 5, 6];

    /// <summary>
    /// A section's prose as the client reads it; <c>null</c> while unwritten.
    /// </summary>
    // The Territory is the agent's ConsultMeFor. The Toolkit has no prose: callers list its tools.
    public static string? Prose(AgentDraft agent, int rail) => rail switch
    {
        1 => AgentRows.Plain(agent.Target),
        2 => AgentRows.Plain(agent.Personality),
        4 => AgentRows.Plain(agent.ConsultMeFor),
        5 => AgentRows.Plain(agent.Instructions),
        6 => AgentRows.Plain(agent.Formatting),
        _ => null
    };
}
