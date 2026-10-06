namespace Alembic.Model;

/// <summary>
/// An agent's seven sections as the shell shows them, keyed by the rail that writes each.
/// </summary>
public static class AgentSections
{
    /// <summary>
    /// The rail whose content is the tools rather than prose.
    /// </summary>
    public const int ToolkitRail = 3;

    /// <summary>
    /// The rail whose content is the workflows rather than prose.
    /// </summary>
    public const int WorkflowsRail = 4;

    /// <summary>
    /// Every section rail, in the order the track walks them.
    /// </summary>
    public static IReadOnlyList<int> All { get; } = [1, 2, 3, 4, 5, 6, 7];

    /// <summary>
    /// A section's prose as the client reads it; <c>null</c> while unwritten.
    /// </summary>
    // The Toolkit and the Workflows have no prose: callers list what they hold.
    public static string? Prose(AgentDraft agent, int rail) => rail switch
    {
        1 => AgentRows.Plain(agent.Target),
        2 => AgentRows.Plain(agent.Personality),
        5 => AgentRows.Plain(agent.Territory),
        6 => AgentRows.Plain(agent.Instructions),
        7 => AgentRows.Plain(agent.Formatting),
        _ => null
    };
}
