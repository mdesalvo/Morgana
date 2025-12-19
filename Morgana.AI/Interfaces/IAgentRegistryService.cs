namespace Morgana.AI.Interfaces
{
    public interface IAgentRegistryService
    {
        Type? ResolveAgentFromIntent(string intent);
        IEnumerable<string> GetAllIntents();
    }
}