using Alembic.Interfaces;
using Alembic.Models;

namespace Alembic.Services;

/// <summary>
/// Holds the Draft of one circuit and a periodic copy of it that Save my work falls back to.
/// </summary>
/// <remarks>The Draft lives and dies with the circuit: nothing is resumed unless the client brings the file back.</remarks>
public sealed class DraftStateService : IDraftStateService, IDisposable
{
    /// <summary>Turns the Draft into the bytes of each snapshot.</summary>
    private readonly IDraftSerializationService drafts;

    /// <summary>Receives the notice of a snapshot that could not be taken.</summary>
    private readonly ILogger logger;

    /// <summary>Fires the periodic snapshot until the circuit closes.</summary>
    private readonly Timer timer;

    /// <summary>
    /// The most recent snapshot, kept only so Save my work is never left with nothing to hand over.
    /// </summary>
    private byte[]? reserve;

    /// <summary>
    /// Binds the state service to the serializer its snapshots go through.
    /// </summary>
    public DraftStateService(IDraftSerializationService drafts, IConfiguration configuration, ILogger logger)
    {
        this.drafts = drafts;
        this.logger = logger;

        // The cadence of the snapshots comes from the workbench configuration.
        TimeSpan autosave = TimeSpan.FromSeconds(configuration.GetValue("Alembic:Work:AutosaveSeconds", 20));

        // The first snapshot fires one interval after the circuit opens since there is no Draft before then.
        timer = new Timer(_ => Snapshot(), null, autosave, autosave);
    }

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

    /// <inheritdoc />
    /// <remarks>Called by the timer and by the Save my work handler, which wants a copy as of the moment the client presses the button.</remarks>
    /// <returns>The freshly serialized bytes or <c>null</c> when there is no Draft or serialization failed, in which case the previous snapshot stands.</returns>
    public byte[]? Snapshot()
    {
        // Before the first Draft there is nothing to keep. The Draft is read once because the timer thread and the circuit both arrive here.
        if (Current is not { } draft)
            return null;

        try
        {
            // The new snapshot replaces the one Save my work would fall back to.
            return reserve = drafts.Serialize(draft);
        }
        catch (Exception ex)
        {
            // A Draft caught mid-edit is a snapshot not taken and the previous one still stands.
            // The failure is logged because the client would otherwise learn of it only from a stale save.
            logger.LogWarning(ex, "A snapshot of the Draft could not be taken");
            // No fresh bytes exist for this attempt.
            return null;
        }
    }

    /// <inheritdoc />
    /// <returns>The most recent snapshot taken by the timer or by an explicit call, without serializing again.</returns>
    public byte[]? Latest() => reserve;

    /// <inheritdoc />
    public void Dispose() => timer.Dispose();
}
