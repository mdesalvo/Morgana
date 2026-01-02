using Morgana.AI.Interfaces;
using System.Reflection;
using System.Text.Json;

namespace Morgana.AI.Services;

/// <summary>
/// Resolves prompts from two sources:
/// 1. morgana.json (framework prompts: Morgana, Classifier, Guard, Presentation)
/// 2. agents.json (domain prompts: billing, contract, troubleshooting, etc.)
/// </summary>
public class ConfigurationPromptResolverService : IPromptResolverService
{
    private readonly Lazy<Records.Prompt[]> morganaPrompts;
    private readonly IAgentConfigurationService agentConfigService;

    public ConfigurationPromptResolverService(IAgentConfigurationService agentConfigService)
    {
        this.agentConfigService = agentConfigService;
        morganaPrompts = new Lazy<Records.Prompt[]>(LoadMorganaPrompts);
    }

    public async Task<Records.Prompt[]> GetAllPromptsAsync()
    {
        // Merge: morgana.json (framework) + domain
        List<Records.Prompt> agentPrompts = await agentConfigService.GetAgentPromptsAsync();
        return [..morganaPrompts.Value, ..agentPrompts];
    }

    public async Task<Records.Prompt> ResolveAsync(string promptID)
    {
        Records.Prompt[] allPrompts = await GetAllPromptsAsync();
        
        Records.Prompt? prompt = allPrompts
            .SingleOrDefault(p => string.Equals(p.ID, promptID, StringComparison.OrdinalIgnoreCase));

        return prompt ?? throw new KeyNotFoundException($"Prompt with ID '{promptID}' not found in morgana.json or agents.json.");
    }

    private static Records.Prompt[] LoadMorganaPrompts()
    {
        // Load only morgana.json (framework prompts: Morgana, Classifier, Guard, Presentation)
        Assembly assembly = Assembly.GetExecutingAssembly();
        string resourceName = assembly.GetManifestResourceNames()
            .First(n => n.EndsWith(".morgana.json", StringComparison.OrdinalIgnoreCase));

        using Stream? stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException("Resource morgana.json not found in Morgana.AI assembly.");
        
        Records.PromptCollection? promptsCollection = JsonSerializer.Deserialize<Records.PromptCollection>(
            stream, 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        return promptsCollection?.Prompts ?? [];
    }
}