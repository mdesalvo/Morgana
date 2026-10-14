using System.ComponentModel;
using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Reads a whole domain at once and reports what only becomes visible there.
/// </summary>
/// <remarks>The pass is advisory: it never blocks an export, since a client who disagrees is often right about their own domain.</remarks>
public interface ICoherenceService
{
    /// <summary>
    /// The classes of defect this pass can be asked for, as its own prompt declares them.
    /// </summary>
    /// <remarks>The checkboxes on Review are rendered from this list, so what is ticked and what the model may answer with are one list.</remarks>
    IReadOnlyList<CoherenceAspect> Aspects { get; }

    /// <summary>
    /// Reviews the whole domain, for the classes of defect asked for.
    /// </summary>
    /// <param name="draft">The domain as it stands.</param>
    /// <param name="aspects">The <see cref="CoherenceAspect.Id"/>s to look for; an empty selection is refused.</param>
    /// <param name="resolved">Findings applied earlier this sitting, so a re-run tells a defect still present from one already dealt with.</param>
    /// <param name="cancellationToken">Stops the pass when the client leaves the page.</param>
    /// <returns>What the pass found, or an empty report when it found nothing.</returns>
    Task<CoherenceReport> ReviewAsync(
        DomainDraft draft,
        IReadOnlyList<string> aspects,
        IReadOnlyList<ResolvedCoherenceFinding>? resolved = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of one coherence pass.
/// </summary>
/// <param name="Findings">What it found, most consequential first.</param>
/// <param name="Error">Why the pass could not run; <c>null</c> when it ran.</param>
public sealed record CoherenceReport(IReadOnlyList<CoherenceFinding> Findings, string? Error = null);

/// <summary>
/// One thing the pass noticed.
/// </summary>
/// <param name="Kind">What kind of incoherence it is.</param>
/// <param name="Where">The agents, intents or variables involved.</param>
/// <param name="What">The observation, in one sentence.</param>
/// <param name="Why">What it will cost at runtime.</param>
/// <param name="Fix">The change proposed, stated concretely enough to apply.</param>
[Description("One defect that is only visible from the whole domain.")]
public sealed record CoherenceFinding(
    [Description("The class of defect.")] string Kind,
    [Description("The agents, intents or variables involved, comma separated.")] string Where,
    [Description("The observation in one sentence of at most twenty-five words.")] string What,
    [Description("What it costs at runtime in one sentence of at most twenty words, quoting a user's message only when that message is the whole point.")] string Why,
    [Description("One change in at most two short sentences, written as a single instruction with no decision left in it for whoever applies it.")] string Fix);

/// <summary>
/// A finding fixed earlier this sitting and what the applier said it changed.
/// </summary>
/// <param name="Finding">What the earlier pass reported.</param>
/// <param name="Resolution">What <see cref="ICoherenceApplyService"/> said it changed.</param>
public sealed record ResolvedCoherenceFinding(CoherenceFinding Finding, string Resolution);
