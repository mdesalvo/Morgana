namespace Alembic.Models;

/// <summary>
/// The card an agent presents to whoever might consult it, projected from the Draft in hand.
/// </summary>
/// <remarks>The card is built from the same three things a running Morgana uses: the intent's name, the territory and the tools.</remarks>
public static class CardProjection
{
    /// <summary>The description a caller reads: the routing phrase until the territory is settled and the territory from then on.</summary>
    /// <remarks>Never both at once, since a caller would weigh its question against two different things.</remarks>
    public static string Description(IntentDraft intent, AgentDraft agent) =>
        // A written territory replaces the routing phrase; a missing one leaves the intent's description.
        AgentRows.Plain(agent.Territory) is { Length: > 0 } territory
            ? territory
            : intent.Description ?? string.Empty;

    /// <summary>The skills the card advertises to an external consumer, one row per tool of the agent.</summary>
    /// <remarks>A colleague inside Morgana is offered the description alone, since an inventory of functions invites ruling out questions the agent has never seen.</remarks>
    public static string Skills(AgentDraft agent) =>
        // One line per tool with the description the author wrote for it.
        string.Join("\n", agent.Tools.Select(tool => $"    skill '{tool.Name}': {tool.Description}"));
}
