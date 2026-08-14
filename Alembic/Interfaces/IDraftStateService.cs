using Alembic.Model;

namespace Alembic.Interfaces;

/// <summary>
/// Holds the Draft the client is currently working on.
/// </summary>
/// <remarks>
/// Scoped, which in Blazor Server means one instance per circuit: two browser tabs are two separate
/// interviews, and the state dies with the connection that owns it. Nothing is persisted and nothing
/// is resumed — durability is the client's, via <see cref="IDraftSerializationService"/> and the file
/// behind Save my work, which they bring back to the landing page to carry on.
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

    /// <summary>
    /// Serializes the Draft as it stands right now, and keeps it as the newest snapshot.
    /// </summary>
    /// <returns>The bytes, or <c>null</c> if there is nothing to snapshot.</returns>
    byte[]? Snapshot();

    /// <summary>
    /// The newest snapshot the autosave took, for the one case where taking a fresh one fails.
    /// </summary>
    /// <returns>The bytes, or <c>null</c> if none has been taken yet.</returns>
    byte[]? Latest();
}
