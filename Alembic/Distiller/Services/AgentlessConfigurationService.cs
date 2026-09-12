using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Distiller.Services;

/// <summary>
/// <see cref="IAgentConfigurationService"/> for a process that hosts no domain: both sources are empty.
/// </summary>
/// <remarks>
/// Alembic is registered against this rather than the framework's own <c>EmbeddedAgentConfigurationService</c>
/// on purpose: the domain Alembic works on is always the one uploaded into the current circuit, never
/// one compiled into this process. The framework's implementation would reach the same empty state by
/// reflecting over every loaded assembly and then warning that no <c>agents.json</c> was found — a
/// true statement about a Morgana deployment that has lost its domain and a misleading one to log
/// here on every startup. Declaring the absence directly costs a scan less and reads as the design it is.
/// </remarks>
public class AgentlessConfigurationService : IAgentConfigurationService
{
    /// <inheritdoc />
    /// <returns>Always an empty list — see the class remarks.</returns>
    public Task<List<Records.IntentDefinition>> GetIntentsAsync() => Task.FromResult(new List<Records.IntentDefinition>());

    /// <inheritdoc />
    /// <returns>Always an empty list — see the class remarks.</returns>
    public Task<List<Records.Prompt>> GetAgentPromptsAsync() => Task.FromResult(new List<Records.Prompt>());
}