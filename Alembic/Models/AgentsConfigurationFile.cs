using Morgana.AI;

namespace Alembic.Models;

/// <summary>
/// The shape of an uploaded <c>agents.json</c>: the intents of the domain and the prompt of each agent.
/// </summary>
/// <remarks>Morgana.AI keeps its own envelope private, so the upload path declares the shape here over the framework's records.</remarks>
/// <param name="Intents">The domain's intents with their description and button.</param>
/// <param name="Agents">The agent prompts, each keyed by the name of the intent it serves.</param>
public sealed record AgentsConfigurationFile(
    List<Records.IntentDefinition> Intents,
    List<Records.Prompt> Agents);
