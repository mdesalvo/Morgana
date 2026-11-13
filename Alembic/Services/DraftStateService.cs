using Alembic.Interfaces;
using Alembic.Models;

namespace Alembic.Services;

/// <summary>
/// The default <see cref="IDraftStateService"/>: the circuit's Draft with a copy of itself a few seconds old, gone with the circuit.
/// Nothing is resumed on the way in, since a workbench that resumed what the client had not asked for would decide what they came to do;
/// the copy exists so Save my work hands over a file even when serializing the Draft as it stands fails.
/// </summary>
public sealed class DraftStateService : IDraftStateService, IDisposable
{
    /// <summary>Turns the Draft into the bytes the client takes away.</summary>
    private readonly IDraftSerializationService drafts;

    /// <summary>Records a snapshot that could not be taken.</summary>
    private readonly ILogger logger;

    /// <summary>Takes a snapshot every <c>Alembic:Work:AutosaveSeconds</c>, stopped with the circuit.</summary>
    private readonly Timer timer;

    /// <summary>The most recent snapshot, kept only so Save my work is never left with nothing to hand over.</summary>
    private byte[]? latest;

    /// <summary>Binds the state service to the serializer its snapshots go through and starts the autosave.</summary>
    /// <param name="drafts">Turns the Draft into the bytes the client takes away.</param>
    /// <param name="configuration">How often to take a snapshot.</param>
    /// <param name="logger">Records a snapshot that could not be taken.</param>
    public DraftStateService(IDraftSerializationService drafts, IConfiguration configuration, ILogger logger)
    {
        this.drafts = drafts;
        this.logger = logger;

        // How far behind the screen the fallback copy may lag.
        TimeSpan autosave = TimeSpan.FromSeconds(configuration.GetValue("Alembic:Work:AutosaveSeconds", 20));

        // The first snapshot fires one interval after the circuit opens, when there may be a Draft at last; it runs on the
        // thread pool rather than the circuit's synchronization context, so a slow serialization never stalls the screen.
        timer = new Timer(_ => Snapshot(), null, autosave, autosave);
    }

    /// <inheritdoc />
    public DomainDraft? Current { get; private set; }

    /// <inheritdoc />
    public event Action? Changed;

    /// <summary>Replaces the Draft under construction and tells every listening component to re-render.</summary>
    public void Set(DomainDraft? draft)
    {
        // Every page of the circuit reads the Draft from here, so each re-renders on the one that replaced it.
        Current = draft;
        Changed?.Invoke();
    }

    /// <summary>
    /// Serializes the Draft as it stands and keeps it as the newest snapshot; <c>null</c> without a Draft or when serialization fails.
    /// The timer calls it on its cadence and Save my work at the moment of the press, the timer being a floor under that call.
    /// </summary>
    public byte[]? Snapshot()
    {
        // Read into a local first: the timer's thread and the circuit's own both arrive here and a
        // Draft replaced between the check and the serialization would throw where nobody is looking.
        if (Current is not { } draft)
            return null;

        try
        {
            // The fresh copy replaces the last one and goes to the caller.
            return latest = drafts.Serialize(draft);
        }
        catch (Exception ex)
        {
            // A Draft caught mid-edit is a snapshot not taken and the one before it still stands.
            // Letting this out of a timer callback would take the process down. Recorded because the
            // floor under Save my work failing is otherwise silent: a client whose snapshots have
            // all failed learns it the day the button hands them the one before them all.
            logger.LogWarning(ex, "A snapshot of the Draft could not be taken");
            return null;
        }
    }

    /// <summary>The most recent snapshot, taken by the timer or by Save my work, read without serializing anything.</summary>
    public byte[]? Latest() => latest;

    /// <summary>Stops the autosave, so a closed circuit leaves no snapshot loop running against a Draft nobody can reach.</summary>
    public void Dispose() => timer.Dispose();
}