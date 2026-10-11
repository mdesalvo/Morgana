using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Authors the half of a tool class the client owns: a working mock of every declared tool.
/// </summary>
/// <remarks>The mock returns plausible domain data so the client can talk to their agent on the first run.</remarks>
public interface IToolMockService
{
    /// <summary>
    /// Writes the client-owned tool class for one agent.
    /// </summary>
    /// <param name="agent">The agent whose toolkit to mock; it must declare at least one tool.</param>
    /// <param name="intentName">The intent that routes to the agent.</param>
    /// <param name="cancellationToken">Stops the completion when the client leaves the page.</param>
    /// <returns>The complete text of <c>{ToolClass}.cs</c>.</returns>
    Task<string> AuthorAsync(AgentDraft agent, string intentName, CancellationToken cancellationToken = default);
}
