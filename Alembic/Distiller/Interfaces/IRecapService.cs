using Distiller.Model;

namespace Distiller.Interfaces;

/// <summary>
/// Composes what an agent's model will really read.
/// </summary>
/// <remarks>
/// The recap is the composed prompt, not a summary of the interview: a summary would be Alembic
/// grading its own homework, whereas this is the framework's own <c>IPromptComposerService</c>
/// producing the same bytes the running agent gets.
/// <para>
/// It is possible at all because <c>ComposeAgentInstructionsAsync</c> takes the domain prompt as a
/// parameter — so the <c>Records.Prompt</c> can be built in memory from a Draft that exists nowhere
/// on disk and belongs to no deployed Morgana.
/// </para>
/// <para>
/// One caveat the client is owed: the framework layer comes from the <c>morgana.json</c> embedded
/// in the Morgana.AI this Alembic was built against. There is no override for it anywhere in the
/// framework — it is an embedded resource — so the recap is true for that version of Morgana and
/// says nothing about a different one.
/// </para>
/// </remarks>
public interface IRecapService
{
    /// <summary>
    /// Composes one agent's system prompt, tool descriptions and hypothetical held-context injection.
    /// </summary>
    /// <param name="agent">The agent to compose.</param>
    /// <returns>What the model reads, split by where in the turn it reads it.</returns>
    Task<AgentRecap> ComposeAsync(AgentDraft agent);
}
