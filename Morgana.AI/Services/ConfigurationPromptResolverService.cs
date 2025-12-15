using Morgana.AI.Interfaces;
using System.Reflection;
using System.Text.Json;

namespace Morgana.AI.Services
{
    public class ConfigurationPromptResolverService : IPromptResolverService
    {
        private readonly Lazy<Records.Prompt[]> configuredPrompts;

        public ConfigurationPromptResolverService()
        {
            configuredPrompts = new Lazy<Records.Prompt[]>(LoadPrompts);
        }

        public Task<Records.Prompt[]> GetAllPromptsAsync()
        {
            return Task.FromResult(configuredPrompts.Value);
        }

        public Task<Records.Prompt> ResolveAsync(string promptID)
        {
            Records.Prompt? prompt = configuredPrompts.Value.FirstOrDefault(p => p.ID == promptID);

            if (prompt == null)
                throw new KeyNotFoundException($"Prompt con ID '{promptID}' non trovato.");

            return Task.FromResult(prompt);
        }

        private static Records.Prompt[] LoadPrompts()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string resourceName = assembly.GetManifestResourceNames()
                                          .First(n => n.EndsWith("prompts.json", StringComparison.Ordinal));

            using Stream? stream = assembly.GetManifestResourceStream(resourceName)
                                    ?? throw new FileNotFoundException("Risorsa prompts.json non trovata nell'assembly.");
            PromptRoot? promptsRoot = JsonSerializer.Deserialize<PromptRoot>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return promptsRoot?.Prompts ?? [];
        }

        private class PromptRoot
        {
            public Records.Prompt[] Prompts { get; set; } = [];
        }
    }
}