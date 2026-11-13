using Morgana.AI;

namespace Alembic.Models;

/// <summary>
/// The on-disk shape of an <c>agents.json</c>: an intent list and an agent-prompt list, both of Morgana's own <see cref="Records"/> types.
/// The framework keeps its envelope private, reading the file from an embedded resource, so Alembic, which reads an upload, declares its own.
/// </summary>
/// <param name="Intents">Intent definitions: Name, Description, Label, DefaultValue.</param>
/// <param name="Agents">Agent prompts, keyed by an ID matching an intent name.</param>
public sealed record AgentsConfigurationFile(
    List<Records.IntentDefinition> Intents,
    List<Records.Prompt> Agents);
