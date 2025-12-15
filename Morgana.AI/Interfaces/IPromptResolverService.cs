using static Morgana.AI.Records;

namespace Morgana.AI.Interfaces
{
    public interface IPromptResolverService
    {
        Task<Prompt[]> GetAllPromptsAsync();
        Task<Prompt> ResolveAsync(string promptID);
    }
}