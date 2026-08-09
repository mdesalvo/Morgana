using Alembic.Model;

namespace Alembic.Interfaces;

/// <summary>
/// Holds the Draft the client is currently working on.
/// </summary>
/// <remarks>
/// Scoped, which in Blazor Server means one instance per circuit: two browser tabs are two separate
/// interviews, and the state dies with the connection that owns it. Nothing here is persisted —
/// durability is the client's, via <see cref="IDraftSerializationService"/>.
/// </remarks>
public interface IDraftStateService
{
    /// <summary>
    /// The Draft under construction, or <c>null</c> before one is imported or started.
    /// </summary>
    DomainDraft? Current { get; }

    /// <summary>
    /// Raised whenever <see cref="Current"/> is replaced, so components can re-render.
    /// </summary>
    event Action? Changed;

    /// <summary>
    /// Replaces the Draft under construction.
    /// </summary>
    /// <param name="draft">The new Draft, or <c>null</c> to clear.</param>
    void Set(DomainDraft? draft);
}
