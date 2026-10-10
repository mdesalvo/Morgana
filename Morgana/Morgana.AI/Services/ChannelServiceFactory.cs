using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IChannelServiceFactory"/> implementation: builds its dispatch table at
/// construction time, then serves <see cref="Resolve"/>/<see cref="IsRegistered"/> as O(1)
/// dictionary lookups. Keys are normalised (trimmed + lowercased) once, up front.
/// </summary>
public class ChannelServiceFactory : IChannelServiceFactory
{
    /// <summary>
    /// The dispatch table, built once at construction: normalised <c>deliveryMode</c> key to the
    /// concrete transport serving it. Ordinal comparer — the keys are already lowercased, so a
    /// case-insensitive one would be paid for nothing.
    /// </summary>
    private readonly IReadOnlyDictionary<string, IChannelService> servicesByDeliveryMode;

    /// <summary>Initialises the factory from the full set of registrations present in DI.</summary>
    /// <param name="registrations">One per concrete <see cref="IChannelService"/>, each declaring its own <c>deliveryMode</c> key.</param>
    /// <exception cref="ArgumentException">Two registrations share the same delivery-mode key.</exception>
    public ChannelServiceFactory(IEnumerable<ChannelServiceRegistration> registrations)
    {
        // Ordinal, not OrdinalIgnoreCase: keys are already lowercased by Normalise below, so an
        // ordinal comparer is both correct and the cheapest option — no need to pay for a
        // case-insensitive comparer on strings that can never differ by case at this point.
        Dictionary<string, IChannelService> table = new(StringComparer.Ordinal);
        foreach (ChannelServiceRegistration registration in registrations)
        {
            // The key this transport will be found under, taken from what its own registration declared.
            string key = Normalise(registration.DeliveryMode);

            // TryAdd returning false means some earlier registration already claimed this exact
            // deliveryMode — fail loud at startup rather than silently letting the last-registered
            // service win and shadow the others, which would be a very confusing runtime surprise.
            if (!table.TryAdd(key, registration.Service))
                throw new ArgumentException(
                    $"Duplicate IChannelService registration for deliveryMode '{key}'. " +
                    "Each delivery mode must be served by exactly one concrete channel service.",
                    nameof(registrations));
        }
        // The dispatch surface is settled: from here the set of usable delivery modes never changes.
        servicesByDeliveryMode = table;
    }

    /// <inheritdoc/>
    public IChannelService Resolve(string deliveryMode)
    {
        // The caller's spelling put through the rule the keys went through, so a channel announcing
        // "Webhook " reaches the webhook transport.
        string key = Normalise(deliveryMode);

        // This throw is not expected to be reachable in practice: MorganaController's
        // start-conversation gate calls IsRegistered before ever accepting a handshake for this
        // deliveryMode, so Resolve should only ever see values that already passed that check. It
        // stays a hard exception rather than a null return because reaching it anyway means that
        // invariant broke somewhere — better to fail loudly than route silently to nothing.
        if (!servicesByDeliveryMode.TryGetValue(key, out IChannelService? service))
            throw new InvalidOperationException(
                $"No IChannelService registered for deliveryMode '{deliveryMode}'. " +
                "The start-conversation gate should have rejected this handshake via IsRegistered.");
        return service;
    }

    /// <summary>
    /// Tells whether a transport serves the delivery mode; an unset mode is a common malformed-handshake shape and answers <c>false</c> instead of throwing.
    /// </summary>
    public bool IsRegistered(string deliveryMode) =>
        !string.IsNullOrWhiteSpace(deliveryMode)
            && servicesByDeliveryMode.ContainsKey(Normalise(deliveryMode));

    /// <summary>
    /// The one rule that every key goes through at construction and at lookup, so "Webhook", "webhook " and "webhook" reach the same transport.
    /// </summary>
    private static string Normalise(string deliveryMode) =>
        deliveryMode.Trim().ToLowerInvariant();
}