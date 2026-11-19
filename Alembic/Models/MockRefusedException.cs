namespace Alembic.Models;

/// <summary>
/// Thrown when the CodeGuard refused the mock of at least one agent twice, so no archive was built.
/// </summary>
public sealed class MockRefusedException : Exception
{
    /// <summary>The agents whose mock stayed out, so the page can send the client to each one.</summary>
    public IReadOnlyList<string> AgentIds { get; }

    /// <summary>Names the agents whose mocks were refused.</summary>
    /// <param name="agentIds">The identifiers of the agents whose mock the CodeGuard refused twice.</param>
    public MockRefusedException(IReadOnlyList<string> agentIds)
        : base($"The mocks of {string.Join(", ", agentIds)} do something their tools do not declare.")
    {
        // Kept apart from the message, which the page does not parse.
        AgentIds = agentIds;
    }
}