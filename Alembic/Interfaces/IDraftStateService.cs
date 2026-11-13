using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Holds the Draft the client is working on, one per circuit, so two browser tabs are two interviews and the state dies with its connection.
/// Durability is the client's, through the file behind Save my work; the autosave snapshot only backs that one button.
/// </summary>
public interface IDraftStateService
{
    /// <summary>The Draft under construction; <c>null</c> before one is imported or started.</summary>
    DomainDraft? Current { get; }

    /// <summary>Raised whenever <see cref="Current"/> is replaced, so components can re-render.</summary>
    event Action? Changed;

    /// <summary>Replaces the Draft under construction.</summary>
    /// <param name="draft">The new Draft; <c>null</c> clears it.</param>
    void Set(DomainDraft? draft);

    /// <summary>Serializes the Draft as it stands and keeps it as the newest snapshot.</summary>
    /// <returns>The bytes; <c>null</c> when there is nothing to snapshot.</returns>
    byte[]? Snapshot();

    /// <summary>The newest snapshot the autosave took, for the one case where taking a fresh one fails.</summary>
    /// <returns>The bytes; <c>null</c> when none has been taken yet.</returns>
    byte[]? Latest();
}