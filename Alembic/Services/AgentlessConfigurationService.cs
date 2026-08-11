using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// <see cref="IAgentConfigurationService"/> for a process that hosts no domain: both sources are empty.
/// </summary>
public class AgentlessConfigurationService : IAgentConfigurationService
{
    /// <inheritdoc />
    public Task<List<Records.IntentDefinition>> GetIntentsAsync() => Task.FromResult(new List<Records.IntentDefinition>());

    /// <inheritdoc />
    public Task<List<Records.Prompt>> GetAgentPromptsAsync() => Task.FromResult(new List<Records.Prompt>());
}