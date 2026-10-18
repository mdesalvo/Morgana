using Morgana.AI;
using Morgana.AI.Interfaces;

namespace PromptHarness.Infrastructure.Engine;

/// <summary>
/// <see cref="IAgentConfigurationService"/> for a process that hosts no domain: both sources are empty.
/// </summary>
/// <remarks>It exists so the judge can resolve morgana.json's own prompts with no domain loaded.</remarks>
public class AgentlessConfigurationService : IAgentConfigurationService
{
    /// <inheritdoc />
    /// <returns>Always an empty list: no intent is compiled into this process.</returns>
    public Task<List<Records.IntentDefinition>> GetIntentsAsync() => Task.FromResult(new List<Records.IntentDefinition>());

    /// <inheritdoc />
    /// <returns>Always an empty list: no agent prompt is compiled into this process.</returns>
    public Task<List<Records.Prompt>> GetAgentPromptsAsync() => Task.FromResult(new List<Records.Prompt>());
}
