namespace Alembic.Models;

/// <summary>
/// One class of defect the coherence pass can be asked to look for.
/// </summary>
/// <remarks>Read from the <c>DomainValidator</c> prompt's <c>Aspects</c> declaration, so the checkboxes and the answer kinds are one list.</remarks>
public sealed class CoherenceAspect
{
    /// <summary>
    /// The <c>kind</c> the pass answers with for this class, e.g. <c>overlapping-intents</c>.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The text beside the checkbox the client ticks.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>One line under the checkbox: what ticking it goes looking for.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>The whole class as authored, which the model reads in the pass's Instructions when the client selects it.</summary>
    public string Description { get; set; } = string.Empty;
}
