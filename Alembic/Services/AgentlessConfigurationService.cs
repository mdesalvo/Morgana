using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Alembic.Services;

/// <summary>
/// The <see cref="IAgentConfigurationService"/> of a process that hosts no domain: both sources are empty.
/// The domain Alembic works on is always the one uploaded into the circuit; the framework's own service would reach the same emptiness
/// by scanning every loaded assembly and then warn on every startup that no <c>agents.json</c> was found.
/// </summary>
public class AgentlessConfigurationService : IAgentConfigurationService
{
    /// <summary>No intents: Alembic serves none of its own.</summary>
    public Task<List<Records.IntentDefinition>> GetIntentsAsync() => Task.FromResult(new List<Records.IntentDefinition>());

    /// <summary>No agent prompts, so the framework prompts alone resolve through this process's resolver.</summary>
    public Task<List<Records.Prompt>> GetAgentPromptsAsync() => Task.FromResult(new List<Records.Prompt>());
}