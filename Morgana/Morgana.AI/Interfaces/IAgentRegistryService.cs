namespace Morgana.AI.Interfaces;

/// <summary>
/// Runtime discovery of agent types via [HandlesIntent] attribute scanning. Builds intent→Type mapping.
/// RouterActor queries for routing. Enables plugin discovery without hardcoding. HandlesIntentAgentRegistryService
/// scans AppDomain at startup; alternatives could use lazy/dynamic loading.
/// </summary>
public interface IAgentRegistryService
{
    /// <summary>
    /// Resolves agent type for intent name from [HandlesIntent] registry. Returns Type if found, null otherwise.
    /// Allows RouterActor to provide user-friendly error messages on null (unrecognized intents).
    /// </summary>
    Type? ResolveAgentFromIntent(string intent);

    /// <summary>
    /// Returns all registered intent names from [HandlesIntent] agents. Used for RouterActor initialization,
    /// config validation, startup diagnostics. Compare results with agents.json to detect config mismatches.
    /// </summary>
    IEnumerable<string> GetAllIntents();
}