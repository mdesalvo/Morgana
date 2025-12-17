namespace Morgana.AI.Interfaces
{
    public interface IAgentRegistryService
    {
        Type? GetAgentType(string intent);
        IEnumerable<string> GetRegisteredIntents();
    }
}