namespace Alembic.Models;

/// <summary>
/// An agent's seven sections as the shell shows them, keyed by the rail that writes each.
/// </summary>
public static class AgentSections
{
    /// <summary>The rail that holds the agent's tools.</summary>
    public const int ToolkitRail = 3;

    /// <summary>The rail that holds the agent's workflows.</summary>
    public const int WorkflowsRail = 4;

    /// <summary>The seven rails in the order the interview settles them.</summary>
    public static IReadOnlyList<int> All { get; } = [1, 2, 3, 4, 5, 6, 7];

    /// <summary>
    /// A section's prose as the client reads it; <c>null</c> while unwritten.
    /// </summary>
    // The Toolkit and the Workflows rails fall to the default arm: they hold no prose and callers list what they contain.
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
