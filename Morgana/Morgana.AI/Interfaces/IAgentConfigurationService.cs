namespace Morgana.AI.Interfaces;

/// <summary>Supplies the domain's intents and agent prompts, which agents.json declares apart from the framework's morgana.json.</summary>
public interface IAgentConfigurationService
{
    /// <summary>Every intent that the domain declares, read by the classifier to choose among them and by the presenter for its buttons.</summary>
    Task<List<Records.IntentDefinition>> GetIntentsAsync();

    /// <summary>Every agent prompt that the domain declares, from which each agent's instructions are composed.</summary>
    Task<List<Records.Prompt>> GetAgentPromptsAsync();
}