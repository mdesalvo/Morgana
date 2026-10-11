namespace Alembic.Models;

/// <summary>
/// The card an agent presents to whoever might consult it, projected from the Draft in hand.
/// </summary>
/// <remarks>
/// A running Morgana publishes one per agent and builds it from three things and no others: the
/// intent's name, the agent's <c>Territory</c> as the description a caller reads and its tools as
/// the skills. Alembic packs the same card while the agent is still being written, so what the
/// interview weighs and what validation reports is what a caller will be handed — the card travels
/// with the agent from the pass that settles its territory to the moment it is let into the domain.
/// </remarks>
public static class CardProjection
{
    /// <summary>The description the card carries at this point of the interview.</summary>
    /// <remarks>
    /// The card exists from the moment the map names the agent and it describes it with the phrase the
    /// classifier routes on until the territory is settled, which is what it says from then onwards.
    /// One description or the other, never both: the routing phrase says which utterances land here
    /// and the territory says what this agent answers for, so a caller reading them together would be
    /// weighing its question against two different things.
    /// </remarks>
    public static string Description(IntentDraft intent, AgentDraft agent) =>
        AgentRows.Plain(agent.Territory) is { Length: > 0 } territory
            ? territory
            : intent.Description ?? string.Empty;

    /// <summary>The skills the card advertises, one row per tool of the agent.</summary>
    /// <remarks>
    /// The skills are the agent's own tools, which is what an external consumer of the card reads. A
    /// colleague inside Morgana is offered the description alone: an inventory of functions invites a
    /// caller to rule out a question the agent has never seen.
    /// </remarks>
    public static string Skills(AgentDraft agent) =>
        string.Join("\n", agent.Tools.Select(tool => $"    skill '{tool.Name}': {tool.Description}"));
}
