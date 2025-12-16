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
            => Task.FromResult(configuredPrompts.Value);

        public Task<Records.Prompt> ResolveAsync(string promptID)
        {
            Records.Prompt? prompt = configuredPrompts.Value.SingleOrDefault(p => string.Equals(p.ID, promptID, StringComparison.OrdinalIgnoreCase));

            if (prompt == null)
                throw new KeyNotFoundException($"Prompt con ID '{promptID}' non trovato.");

            return Task.FromResult(prompt);
        }

        private static Records.Prompt[] LoadPrompts()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string resourceName = assembly.GetManifestResourceNames()
                                          .First(n => n.EndsWith("prompts.json", StringComparison.OrdinalIgnoreCase));

            using Stream? stream = assembly.GetManifestResourceStream(resourceName)
                                    ?? throw new FileNotFoundException("Risorsa prompts.json non trovata nell'assembly.");
            Records.PromptCollection? promptsCollection = JsonSerializer.Deserialize<Records.PromptCollection>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return promptsCollection?.Prompts ?? [];
        }
    }
}