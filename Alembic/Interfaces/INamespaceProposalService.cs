using Alembic.Models;

namespace Alembic.Interfaces;

/// <summary>
/// Proposes the root namespace a domain is delivered in, named after the client's business.
/// A proposal is only ever shown for the client to read and change, so a missing one is an ordinary outcome and never a failure.
/// </summary>
public interface INamespaceProposalService
{
    /// <summary>Proposes one namespace segment for the domain from what is known of the business and its intents.</summary>
    /// <param name="draft">The domain whose business and intents the name is drawn from; it is read and never changed.</param>
    /// <param name="cancellationToken">Cancels the underlying completion.</param>
    /// <returns>The proposed namespace; <c>null</c> when there is none to offer.</returns>
    Task<string?> ProposeAsync(DomainDraft draft, CancellationToken cancellationToken = default);
}