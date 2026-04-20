using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IChannelServiceFactory"/> implementation: builds its dispatch table at
/// construction time from the <see cref="ChannelServiceRegistration"/> entries provided by DI,
/// then serves <see cref="Resolve"/> / <see cref="IsRegistered"/> as O(1) dictionary lookups.
/// </summary>
/// <remarks>
/// <para>Keys are normalised (trimmed + lowercased) once at construction so lookups never have
/// to normalise again. Duplicate keys across registrations cause an <see cref="ArgumentException"/>
/// at startup — two channel services claiming the same <c>deliveryMode</c> would be a silent
/// ambiguity at dispatch, better caught loud at DI build-time.</para>
/// </remarks>
public class ChannelServiceFactory : IChannelServiceFactory
{
    private readonly IReadOnlyDictionary<string, IChannelService> servicesByDeliveryMode;

    /// <summary>
    /// Initialises the factory from the full set of registrations present in DI.
    /// </summary>
    /// <param name="registrations">Every <see cref="ChannelServiceRegistration"/> registered as
    /// a singleton. Each concrete <see cref="IChannelService"/> declares its own
    /// <c>deliveryMode</c> key via this record.</param>
    /// <exception cref="ArgumentException">Two registrations share the same delivery-mode key.</exception>
    public ChannelServiceFactory(IEnumerable<ChannelServiceRegistration> registrations)
    {
        Dictionary<string, IChannelService> table = new(StringComparer.Ordinal);
        foreach (ChannelServiceRegistration registration in registrations)
        {
            string key = Normalise(registration.DeliveryMode);
            if (!table.TryAdd(key, registration.Service))
                throw new ArgumentException(
                    $"Duplicate IChannelService registration for deliveryMode '{key}'. " +
                    "Each delivery mode must be served by exactly one concrete channel service.",
                    nameof(registrations));
        }
        servicesByDeliveryMode = table;
    }

    /// <inheritdoc/>
    public IChannelService Resolve(string deliveryMode)
    {
        string key = Normalise(deliveryMode);
        if (!servicesByDeliveryMode.TryGetValue(key, out IChannelService? service))
            throw new InvalidOperationException(
                $"No IChannelService registered for deliveryMode '{deliveryMode}'. " +
                "The start-conversation gate should have rejected this handshake via IsRegistered.");
        return service;
    }

    /// <inheritdoc/>
    public bool IsRegistered(string deliveryMode) =>
        !string.IsNullOrWhiteSpace(deliveryMode)
        && servicesByDeliveryMode.ContainsKey(Normalise(deliveryMode));

    private static string Normalise(string deliveryMode) =>
        deliveryMode.Trim().ToLowerInvariant();
}
