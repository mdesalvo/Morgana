namespace Morgana.AI.Interfaces;

/// <summary>
/// Supplies the domain half of the configuration: the intents that users can be routed to and the prompts of the agents that handle them.
/// The framework's own prompts are not part of it. An implementation may aggregate several sources.
/// </summary>
public interface IAgentConfigurationService
{
    /// <summary>
    /// Returns the domain's intent definitions. An absent configuration yields an empty list, which leaves the
    /// system able to classify only as <see cref="Constants.Intents.Other"/>.
    /// </summary>
    Task<List<Records.IntentDefinition>> GetIntentsAsync();

    /// <summary>
    /// Returns the domain's agent prompts, one per handled intent. An absent configuration yields an empty list.
    /// </summary>
    Task<List<Records.Prompt>> GetAgentPromptsAsync();
}