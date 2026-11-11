using Microsoft.Extensions.AI;
using Morgana.AI.Attributes;

namespace Morgana.AI.Interfaces;

/// <summary>Keeps the connections to the MCP servers that agents declare and lists the tools each one offers.</summary>
public interface IMCPClientRegistryService : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// The tools of the declared server as functions that an agent may hold for its whole life.
    /// MCP lets a server end its session at any moment and requires the client to open a new one, so an implementation
    /// reopens the session when discovery meets an ended one and keeps every returned tool callable across later ends.
    /// A tool call may run again on the new session only when it provably did not run on the old one: a call that may
    /// have taken effect is never repeated.
    /// </summary>
    /// <param name="serverAttribute">The server declaration carried by the agent class.</param>
    /// <param name="cancellationToken">Abandons discovery; a cancelled call never counts as an ended session.</param>
    /// <returns>The server's tools, under the names and schemas that the server declares.</returns>
    Task<IList<AIFunction>> DiscoverResilientToolsAsync(UsesMCPServerAttribute serverAttribute, CancellationToken cancellationToken = default);
}