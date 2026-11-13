using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Authors the half of a tool class the client owns: a working mock of every declared tool.
/// A mock and not a stub, so on the first run the client can talk to their agent and judge its prose; the data must be specific to the domain,
/// which is what a model writes and a template cannot.
/// </summary>
public interface IToolMockService
{
    /// <summary>Writes the client-owned tool class for one agent.</summary>
    /// <param name="agent">The agent whose toolkit to mock, which must declare at least one tool.</param>
    /// <param name="intentName">The intent that routes to it.</param>
    /// <param name="cancellationToken">Stops the model call.</param>
    /// <returns>The complete text of <c>{ToolClass}.cs</c>.</returns>
    Task<string> AuthorAsync(AgentDraft agent, string intentName, CancellationToken cancellationToken = default);
}