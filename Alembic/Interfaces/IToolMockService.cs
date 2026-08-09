using Alembic.Model;

namespace Alembic.Interfaces;

/// <summary>
/// Authors the half of a tool class the client owns: a working mock of every declared tool.
/// </summary>
/// <remarks>
/// <para>
/// A mock and not a stub, and the difference is the whole point of the turnkey promise: when the
/// archive is dropped into a plugin project and Morgana starts, the client must be able to
/// <em>talk to their agent</em> — see it call a tool, get plausible domain data back, and present
/// it in the prose Alembic wrote for it. A stub returning <c>NotImplementedException</c> makes the
/// prose unreviewable, and the prose is what the whole interview was for.
/// </para>
/// <para>
/// This is the one emitted artifact a template cannot write. Invented invoices, a diary with
/// believable gaps, stock levels that vary — the data has to be specific to the domain to be worth
/// looking at, and specific-to-the-domain is exactly what a language model is for and a template
/// is not.
/// </para>
/// </remarks>
public interface IToolMockService
{
    /// <summary>
    /// Writes the client-owned tool class for one agent.
    /// </summary>
    /// <param name="agent">The agent whose toolkit to mock. Must declare at least one tool.</param>
    /// <param name="intentName">The intent that routes to it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The complete text of <c>{ToolClass}.cs</c>.</returns>
    Task<string> AuthorAsync(AgentDraft agent, string intentName, CancellationToken cancellationToken = default);
}
