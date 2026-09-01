using Distiller.Interfaces;
using Distiller.Model;

namespace Distiller.Services;

/// <summary>
/// Default <see cref="IDraftStateService"/>: the Draft in the circuit, with a recent copy of itself.
/// </summary>
/// <remarks>
/// <para>
/// The Draft lives here, in this circuit, and goes when it goes. Nothing survives a restart and
/// nothing is picked up on the way in: the landing page is the same door for everybody, and coming
/// back to unfinished work means bringing the file back to it. A workbench that silently resumed
/// something the client had not asked to resume would be deciding for them what they had come to do.
/// </para>
/// <para>
/// What the timer keeps is a copy of the last few seconds, and it exists for one gesture: Save my
/// work serializes the Draft as it stands when the button is pressed, and falls back to this when it
/// cannot — a Draft caught mid-write, a serialization that throws. The client gets a file either way,
/// at worst <c>AutosaveSeconds</c> behind what is on their screen.
/// </para>
/// <para>
/// The service is registered <b>Scoped</b>, so its lifetime is exactly one Blazor Server circuit:
/// one instance per browser tab, created when the circuit opens and disposed when it closes —
/// whether that is a clean navigation away or the connection dropping (a suspended laptop, a proxy
/// closing an idle socket). <see cref="Dispose"/> stops the timer either way, so a dead circuit
/// never leaves a snapshot loop running against a Draft nobody can reach any more.
/// </para>
/// </remarks>
public sealed class DraftStateService : IDraftStateService, IDisposable
{
    private readonly IDraftSerializationService drafts;
    private readonly Timer timer;

    /// <summary>
    /// The most recent snapshot, kept only so Save my work is never left with nothing to hand over.
    /// </summary>
    private byte[]? latest;

    /// <summary>
    /// Binds the state service to the serializer its snapshots go through.
    /// </summary>
    /// <param name="drafts">Turns the Draft into the bytes the client takes away.</param>
    /// <param name="configuration">How often to take a snapshot.</param>
    public DraftStateService(IDraftSerializationService drafts, IConfiguration configuration)
    {
        this.drafts = drafts;

        TimeSpan autosave = TimeSpan.FromSeconds(configuration.GetValue("Alembic:Work:AutosaveSeconds", 20));

        // Same interval for the due time and the period: the first snapshot fires one AutosaveSeconds
        // after the circuit opens (there is nothing to snapshot before then anyway — Current starts
        // null), and every one after that on the same cadence, on the .NET thread pool rather than
        // the circuit's own synchronization context.
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
    /// <remarks>
    /// Called both by the timer, on its own cadence, and directly by <c>SaveWork</c>'s own handler
    /// for an up-to-the-second copy at the moment the client actually presses the button — the timer
    /// is a floor under that call, not a replacement for it.
    /// </remarks>
    /// <returns>The freshly serialized bytes, or <c>null</c> when there is no Draft yet or serialization
    /// failed — in the latter case <see cref="latest"/> is left holding whatever snapshot preceded it.</returns>
    public byte[]? Snapshot()
    {
        // Read into a local first: the timer's thread and the circuit's own both arrive here, and a
        // Draft replaced between the check and the serialization would throw where nobody is looking.
        if (Current is not { } draft)
            return null;

        try
        {
            return latest = drafts.Serialize(draft);
        }
        catch (Exception)
        {
            // A Draft caught mid-edit is a snapshot not taken, and the one before it still stands.
            // Letting this out of a timer callback would take the process down.
            return null;
        }
    }

    /// <inheritdoc />
    /// <returns>The most recent snapshot taken, by timer or by an explicit <see cref="Snapshot"/>
    /// call — never re-serializes on its own, so this is a cheap read even mid-turn.</returns>
    public byte[]? Latest() => latest;

    /// <inheritdoc />
    public void Dispose() => timer.Dispose();
}
