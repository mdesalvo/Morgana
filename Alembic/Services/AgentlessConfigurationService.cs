using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// <see cref="IAgentConfigurationService"/> for a process that hosts no domain: both sources are empty.
/// </summary>
/// <remarks>The domain Alembic works on is the one in the client's draft, never one compiled into this process.</remarks>
public class AgentlessConfigurationService : IAgentConfigurationService
{
    /// <inheritdoc />
    /// <returns>Always an empty list: no intent is compiled into this process.</returns>
    public Task<List<Records.IntentDefinition>> GetIntentsAsync() => Task.FromResult(new List<Records.IntentDefinition>());

    /// <inheritdoc />
    /// <returns>Always an empty list: no agent prompt is compiled into this process.</returns>
    public Task<List<Records.Prompt>> GetAgentPromptsAsync() => Task.FromResult(new List<Records.Prompt>());
}