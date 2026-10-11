using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Composes what an agent's model will really read, as the framework's own composer produces it.
/// </summary>
/// <remarks>The framework layer comes from the <c>morgana.json</c> embedded in the Morgana.AI that this Alembic was built against.</remarks>
public interface IRecapService
{
    /// <summary>
    /// Composes one agent's system prompt and the description and parameters of each of its tools.
    /// </summary>
    /// <param name="agent">The agent to compose.</param>
    /// <param name="consultedByOthers">True when another agent declares it may consult this one, since the colleague policy then reaches this prompt too.</param>
    /// <returns>What the model reads, split by where in the turn it reads it.</returns>
    Task<AgentRecap> ComposeAsync(AgentDraft agent, bool consultedByOthers = false);
}
