namespace Alembic.Models;

/// <summary>
/// One class of defect the coherence pass can be asked to look for, read off the DomainValidator prompt's own Aspects declaration.
/// The checkboxes, the prose the model is handed and the kinds it may answer with are one list; the client reads <see cref="Summary"/>, the model <see cref="Description"/>.
/// </summary>
public sealed class CoherenceAspect
{
    /// <summary>The <c>kind</c> the pass answers with for this class, such as <c>overlapping-intents</c>.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What the checkbox reads.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>One line under the checkbox: what ticking it goes looking for.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>The whole class as authored, spliced into the pass's Instructions when selected.</summary>
    public string Description { get; set; } = string.Empty;
}
