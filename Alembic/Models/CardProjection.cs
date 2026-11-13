namespace Alembic.Models;

/// <summary>
/// The card an agent presents to whoever might consult it, projected from the Draft in hand as a running Morgana builds it.
/// Morgana builds it from the intent's name, the Territory as the description and the tools as skills, so what the interview weighs is what a caller is handed.
/// </summary>
public static class CardProjection
{
    /// <summary>
    /// The card as a caller meets it at this point of the interview.
    /// It describes the agent with the routing phrase until the territory is settled and with the territory from then on, never both at once.
    /// </summary>
    public static string Render(IntentDraft intent, AgentDraft agent)
    {
        // A routing phrase says which utterances land here and a territory what the agent answers for: shown together, a caller would weigh two things.
        string description = AgentRows.Plain(agent.Territory) is { Length: > 0 } territory
            ? territory
            : intent.Description ?? string.Empty;

        // The skills an external consumer of the card reads; a colleague inside Morgana is offered the description alone.
        string skills = agent.Tools.Count == 0
            ? "    skills advertised: none — this agent declares no tool of its own"
            : string.Join("\n", agent.Tools.Select(tool => $"    skill '{tool.Name}': {tool.Description}"));

        // The card in the words the model reads it in.
        return $"The card '{intent.Name}' presents to anyone who might consult it:\n"
               + $"    what it answers for: {description}\n{skills}";
    }
}