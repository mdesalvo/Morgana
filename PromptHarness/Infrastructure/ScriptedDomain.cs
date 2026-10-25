using Morgana.AI;
using Morgana.AI.Interfaces;

namespace PromptHarness.Infrastructure;

/// <summary>A deployment whose domain declares no intents and the given agent prompts.</summary>
/// <param name="prompts">The agent prompts the domain returns.</param>
public sealed class ScriptedDomain(params Records.Prompt[] prompts) : IAgentConfigurationService
{
    /// <inheritdoc />
    public Task<List<Records.IntentDefinition>> GetIntentsAsync() => Task.FromResult(new List<Records.IntentDefinition>());

    /// <inheritdoc />
    public Task<List<Records.Prompt>> GetAgentPromptsAsync() => Task.FromResult(new List<Records.Prompt>(prompts));
}
