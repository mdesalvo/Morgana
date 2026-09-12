using Morgana.AI;

namespace Distiller.Model;

/// <summary>
/// The on-disk shape of an <c>agents.json</c>: an intent list and an agent-prompt list.
/// </summary>
/// <remarks>
/// This is the file envelope, not a domain model — everything inside it is Morgana's own
/// <see cref="Records"/> types, so parsing an uploaded configuration costs nothing and there is no
/// second representation of a prompt or a tool to keep in step.
/// <para>
/// The envelope has to be redeclared here only because Morgana.AI keeps its equivalent
/// (<c>EmbeddedAgentConfigurationService.AgentConfiguration</c>) private: the framework reads
/// <c>agents.json</c> from an embedded resource and never needs to name the shape publicly.
/// Alembic reads it from an upload, which is exactly the case that framework path does not serve.
/// </para>
/// </remarks>
/// <param name="Intents">Intent definitions: Name, Description, Label, DefaultValue.</param>
/// <param name="Agents">Agent prompts, keyed by an ID matching an intent name.</param>
public sealed record AgentsConfigurationFile(
    List<Records.IntentDefinition> Intents,
    List<Records.Prompt> Agents);
