using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Renders a Draft's agents and toolkits as the C# sources a Morgana plugin is made of, by deterministic template.
/// The same Draft emits the same bytes, so a re-run against an evolved domain diffs to exactly the change, which is what makes overwriting a generated file safe.
/// The domain mocks, which a template writes badly, are <see cref="IToolMockService"/>'s.
/// </summary>
public interface ICodeEmitService
{
    /// <summary>Emits every generated source for one agent.</summary>
    /// <param name="agent">The agent to emit.</param>
    /// <param name="intentName">The intent that routes to it, the <c>[HandlesIntent]</c> argument.</param>
    /// <returns>The agent's file and, where it declares native tools, its tool class.</returns>
    IReadOnlyList<EmittedFile> Emit(AgentDraft agent, string intentName);
}

/// <summary>One file in the downloaded archive.</summary>
/// <param name="Path">Path inside the archive, forward-slashed.</param>
/// <param name="Content">The file's text.</param>
/// <param name="Ownership">Who may edit it once it is in the client's tree.</param>
public sealed record EmittedFile(string Path, string Content, FileOwnership Ownership);

/// <summary>
/// Who owns an emitted file after it lands in the client's tree, as its name tells.
/// Alembic never sees that tree, so the convention travels in the file names and needs no enforcing: a signature drifting between the two halves does not compile.
/// </summary>
public enum FileOwnership
{
    /// <summary>Alembic's, named <c>*.g.cs</c> and regenerated in full every time, so an edit here is lost.</summary>
    Generated,

    /// <summary>The client's, named <c>*.cs</c>: written once as a working mock and never produced again for an agent that already has one.</summary>
    Client
}