using Distiller.Model;

namespace Distiller.Interfaces;

/// <summary>
/// Renders a Draft's agents and toolkits as the C# sources a Morgana plugin is made of.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is <b>deterministic template</b>. The same Draft emits the same bytes, so
/// re-running Alembic against an evolved domain produces a diff that is exactly the change and
/// nothing else — which is the only reason a regenerated file can be safe to overwrite.
/// </para>
/// <para>
/// What a template writes badly is a domain mock, and that is precisely what is <em>not</em> here:
/// see <see cref="IToolMockService"/>. The split between the two services is the same split the
/// archive carries as a file-name convention.
/// </para>
/// </remarks>
public interface ICodeEmitService
{
    /// <summary>
    /// Emits every generated source for one agent.
    /// </summary>
    /// <param name="agent">The agent to emit.</param>
    /// <param name="intentName">The intent that routes to it — the <c>[HandlesIntent]</c> argument.</param>
    /// <returns>One or two files: the agent, and its tool class where it declares native tools.</returns>
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
/// <remarks>
/// The convention travels <b>inside the archive</b>, in the file names themselves, because Alembic
/// never sees the client's tree and cannot enforce anything about it. It does not need to: a
/// signature that drifts between the two halves is a compile error, and a tool whose declaration
/// stops matching its method fails Morgana's startup in
/// <c>MorganaToolAdapter.AddTool</c>. The convention only has to be legible.
/// </remarks>
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
