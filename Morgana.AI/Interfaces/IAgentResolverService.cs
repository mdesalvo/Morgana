namespace Morgana.AI.Interfaces
{
    public interface IAgentResolverService
    {
        Type? ResolveAgentType(string intent);
        IEnumerable<string> ResolveIntents();
    }
}