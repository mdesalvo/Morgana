using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Renders a Draft's agents and toolkits as the C# sources a Morgana plugin is made of.
/// </summary>
/// <remarks>The same draft emits the same bytes, so a regenerated file differs only by the change.</remarks>
public interface ICodeEmitService
{
    /// <summary>
    /// Emits every generated source for one agent.
    /// </summary>
    /// <param name="agent">The agent to emit.</param>
    /// <param name="intentName">The intent that routes to the agent, which is the <c>[HandlesIntent]</c> argument.</param>
    /// <returns>The agent, one class per workflow and its tool class where it declares native tools.</returns>
    IReadOnlyList<EmittedFile> Emit(AgentDraft agent, string intentName);
}

/// <summary>
/// One file in the downloaded archive.
/// </summary>
/// <param name="Path">Path inside the archive, forward-slashed.</param>
/// <param name="Content">The file's text.</param>
/// <param name="Ownership">Who may edit it once it is in the client's tree.</param>
public sealed record EmittedFile(string Path, string Content, FileOwnership Ownership);

/// <summary>
/// Who owns an emitted file after it lands in the client's tree.
/// </summary>
/// <remarks>The convention travels in the file names, since Alembic never sees the client's tree.</remarks>
public enum FileOwnership
{
    /// <summary>
    /// Alembic's, named <c>*.g.cs</c>. Regenerated in full every time, so an edit here is lost.
    /// </summary>
    Generated,

    /// <summary>
    /// The client's, named <c>*.cs</c>. Written once as a working mock, then theirs forever —
    /// Alembic will not produce it again for an agent that already has one.
    /// </summary>
    Client
}
