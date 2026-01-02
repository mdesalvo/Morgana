namespace Morgana.AI.Interfaces;

/// <summary>
/// Service for loading domain-specific agent configuration.
/// Loads intents and agent prompts, depending on specific implementation strategy (e.g: agents.json)
/// Provides separation between framework (morgana.json) and domain.
/// </summary>
public interface IAgentConfigurationService
{
    /// <summary>
    /// Load intents from agents.json (domain configuration).
    /// Returns intent definitions for classifier and presentation.
    /// </summary>
    /// <returns>List of intent definitions, or empty list if agents.json not found</returns>
    Task<List<Records.IntentDefinition>> GetIntentsAsync();
    
    /// <summary>
    /// Load agent prompts from agents.json (domain configuration).
    /// Returns prompts for domain-specific agents (billing, contract, etc.).
    /// </summary>
    /// <returns>List of agent prompts, or empty list if agents.json not found</returns>
    Task<List<Records.Prompt>> GetAgentPromptsAsync();
}