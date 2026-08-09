using Alembic.Interfaces;
using Alembic.Model;

namespace Alembic.Services;

/// <summary>
/// Default <see cref="IDraftStateService"/>: in-memory, per circuit.
/// </summary>
public class DraftStateService : IDraftStateService
{
    /// <inheritdoc />
    public DomainDraft? Current { get; private set; }

    /// <inheritdoc />
    public event Action? Changed;

    /// <inheritdoc />
    public void Set(DomainDraft? draft)
    {
        Current = draft;
        Changed?.Invoke();
    }
}
