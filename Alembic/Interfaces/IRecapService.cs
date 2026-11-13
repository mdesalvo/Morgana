using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Composes what an agent's model will really read, through the framework's own <c>IPromptComposerService</c>.
/// It is the composed prompt rather than a summary of the interview, which would be Alembic grading its own homework; the framework layer is the
/// <c>morgana.json</c> embedded in the Morgana.AI this Alembic was built against, so the recap is true for that version of Morgana alone.
/// </summary>
public interface IRecapService
{
    /// <summary>Composes one agent's system prompt, tool descriptions and hypothetical held-context injection.</summary>
    /// <param name="agent">The agent to compose.</param>
    /// <param name="consultedByOthers">
    /// True when another agent of the domain may consult this one: the framework renders its consultation policy for either end of that relation,
    /// so an agent that only answers colleagues still reads it.
    /// </param>
    /// <returns>What the model reads, split by where in the turn it reads it.</returns>
    Task<AgentRecap> ComposeAsync(AgentDraft agent, bool consultedByOthers = false);
}