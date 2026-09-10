namespace Distiller.Model;

/// <summary>
/// The card an agent presents to whoever might consult it, projected from the Draft in hand.
/// </summary>
/// <remarks>
/// A running Morgana publishes one per agent and builds it from three things and no others: the
/// intent's name, the agent's <c>ConsultMeFor</c> as the description a caller reads and its tools as
/// the skills. Alembic packs the same card while the agent is still being written, so what the
/// interview weighs and what validation reports is what a caller will be handed — the card travels
/// with the agent from the pass that settles its territory to the moment it is let into the domain.
/// </remarks>
public static class CardProjection
{
    /// <summary>The card as a caller meets it at this point of the interview.</summary>
    /// <remarks>
    /// The card exists from the moment the map names the desk and it describes it with the phrase the
    /// classifier routes on until the territory is settled, which is what it says from then onwards.
    /// One description or the other, never both: the routing phrase says which utterances land here
    /// and the territory says what this desk answers for, so a caller reading them together would be
    /// weighing its question against two different things.
    /// <para>
    /// The skills are the agent's own tools, which is what an external consumer of the card reads. A
    /// colleague inside Morgana is offered the description alone: an inventory of functions invites a
    /// caller to rule out a question the desk has never seen.
    /// </para>
    /// </remarks>
    public static string Render(IntentDraft intent, AgentDraft agent)
    {
        string description = AgentRows.Plain(agent.ConsultMeFor) is { Length: > 0 } territory
            ? territory
            : intent.Description ?? string.Empty;

        string skills = agent.Tools.Count == 0
            ? "    skills advertised: none — this desk declares no tool of its own"
            : string.Join("\n", agent.Tools.Select(tool => $"    skill '{tool.Name}': {tool.Description}"));

        return $"The card '{intent.Name}' presents to anyone who might consult it:\n"
               + $"    what it answers for: {description}\n{skills}";
    }
}
