namespace Distiller.Model;

/// <summary>
/// One class of defect the coherence pass can be asked to look for.
/// </summary>
/// <remarks>
/// Read off the <c>DomainValidator</c> prompt's own <c>Aspects</c> declaration rather than listed
/// again in C#, for the same reason a pass's tools are read off its <c>Tools</c> declaration: the
/// checkboxes the client ticks, the prose the model is handed and the <c>kind</c> values it is
/// allowed to answer with all have to be the same list, and a second copy is a list that drifts.
/// <para>
/// The split between <see cref="Summary"/> and <see cref="Description"/> is the split between the
/// two readers. The client reads a line to decide whether to tick the box; the model reads the whole
/// block, which is the authored prose that used to sit inline in the pass's Instructions.
/// </para>
/// </remarks>
public sealed class CoherenceAspect
{
    /// <summary>
    /// The <c>kind</c> the pass answers with for this class, e.g. <c>overlapping-intents</c>.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What the checkbox reads.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>One line under the checkbox: what ticking it goes looking for.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>The whole class as authored, spliced into the pass's Instructions when selected.</summary>
    public string Description { get; set; } = string.Empty;
}
