using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>Finds the transport that serves a delivery mode, on a table settled at startup with trimmed and lowercased keys.</summary>
public class ChannelServiceFactory : IChannelServiceFactory
{
    /// <summary>The transport serving each normalised <c>deliveryMode</c>.</summary>
    private readonly IReadOnlyDictionary<string, IChannelService> servicesByDeliveryMode;

    /// <summary>Settles the table from every transport registered in DI.</summary>
    /// <param name="registrations">One per concrete <see cref="IChannelService"/>, each declaring its own <c>deliveryMode</c> key.</param>
    /// <exception cref="ArgumentException">Two registrations share the same delivery-mode key.</exception>
    public ChannelServiceFactory(IEnumerable<ChannelServiceRegistration> registrations)
    {
        // Every key goes through Normalise, so the keys can never differ by case.
        Dictionary<string, IChannelService> table = new(StringComparer.Ordinal);
        foreach (ChannelServiceRegistration registration in registrations)
        {
            // The key this transport will be found under, taken from what its own registration declared.
            string key = Normalise(registration.DeliveryMode);

            // Two transports claiming one delivery mode are refused at startup: otherwise one would silently shadow the other.
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

        // The start of a conversation refuses a delivery mode that no transport serves, so reaching this means that gate broke:
        // the message fails loudly instead of being routed to nothing.
        if (!servicesByDeliveryMode.TryGetValue(key, out IChannelService? service))
            throw new InvalidOperationException(
                $"No IChannelService registered for deliveryMode '{deliveryMode}'. " +
                "The start-conversation gate should have rejected this handshake via IsRegistered.");

        // The transport that carries the conversation's messages.
        return service;
    }

    /// <inheritdoc/>
    public bool IsRegistered(string deliveryMode) =>
        // A handshake with no delivery mode is a plain refusal, never an exception from inside the check.
        !string.IsNullOrWhiteSpace(deliveryMode)
            && servicesByDeliveryMode.ContainsKey(Normalise(deliveryMode));

    /// <summary>
    /// Puts a delivery mode through the rule that every key of the table goes through, at construction and at lookup alike.
    /// "Webhook", "webhook " and "webhook" thus reach the same transport however a channel spells its mode.
    /// </summary>
    private static string Normalise(string deliveryMode) =>
        deliveryMode.Trim().ToLowerInvariant();
}