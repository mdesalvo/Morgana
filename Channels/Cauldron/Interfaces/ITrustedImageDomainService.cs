using Morgana.Contracts;

namespace Cauldron.Interfaces;

/// <summary>
/// Decides whether an image may be shown according to the domains listed in configuration and reports the ones withheld.
/// </summary>
public interface ITrustedImageDomainService
{
    /// <summary>
    /// Returns true only for an absolute https URL on the default port whose host is listed in <c>Cauldron:TrustedImageDomains</c>.
    /// </summary>
    bool IsServedFromTrustedDomain(string? imageUrl);

    /// <summary>
    /// Records a metric and a Warning for each image of the reply that does not come from a listed domain.
    /// </summary>
    void ReportImagesFromUntrustedDomains(ChannelMessage reply);
}