namespace Morgana.AI.Interfaces;

/// <summary>
/// Loads domain-specific agent configuration (intents+prompts) from external sources (agents.json, DB, API, etc).
/// Separates framework (morgana.json) from domain (agents.json). EmbeddedAgentConfigurationService loads embedded agents.json.
/// Used by Classifier (intent classification), Presentation (intent buttons), MorganaAgentAdapter (agent prompts).
/// </summary>
public interface IAgentConfigurationService
{
    /// <summary>
    /// Returns intent definitions from agents.json (Name, Description, Label, DefaultValue). Used by ClassifierActor
    /// for LLM classification and Presentation for quick reply buttons. Returns empty list on missing config; system
    /// supports graceful degradation with only the <see cref="Constants.Intents.Other"/> intent if needed.
    /// </summary>
    Task<List<Records.IntentDefinition>> GetIntentsAsync();

    /// <summary>
    /// Returns agent prompts from agents.json (Content, Instructions, Personality, Tools, AdditionalProperties).
    /// Used by MorganaAgentAdapter to compose full agent instructions. Supports multi-source aggregation (embedded
    /// resources, files, DB) for modular domain configs. Returns empty list if config missing.
    /// </summary>
    Task<List<Records.Prompt>> GetAgentPromptsAsync();
}