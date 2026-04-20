namespace Morgana.AI.Interfaces;

/// <summary>
/// Factory that resolves the concrete <see cref="IChannelService"/> implementation for a given
/// <c>deliveryMode</c> declared by the channel at the conversation-start handshake.
/// </summary>
/// <remarks>
/// <para><strong>Why a factory:</strong></para>
/// <para>Morgana serves multiple channel profiles (SignalR for rich UIs, HTTP webhook for
/// plain-text clients, …). Each profile maps to a concrete <see cref="IChannelService"/>.
/// Instead of hard-coding the mapping in a switch or tying dispatch to the free-form
/// <see cref="Records.ChannelCoordinates.ChannelName"/> (client-controlled, potentially infinite),
/// the factory receives every transport at DI registration keyed on its <c>deliveryMode</c>
/// and resolves the right one per-conversation via
/// <see cref="Records.ChannelCoordinates.DeliveryMode"/>.</para>
///
/// <para><strong>Finite-by-registration:</strong></para>
/// <para>The set of valid <c>deliveryMode</c> keys is defined by what's registered in DI — not
/// by an enum in the codebase. Adding a new transport means adding one registration; no contract
/// file needs to move.</para>
///
/// <para><strong>Fail-loud:</strong></para>
/// <para><see cref="Resolve"/> throws <see cref="InvalidOperationException"/> if the key is not
/// registered. The start-conversation gate should consult <see cref="IsRegistered"/> first and
/// reject unknown keys with a 400 — reaching <see cref="Resolve"/> with an unregistered key at
/// send-time is an internal invariant violation.</para>
/// </remarks>
public interface IChannelServiceFactory
{
    /// <summary>
    /// Resolves the <see cref="IChannelService"/> registered under <paramref name="deliveryMode"/>.
    /// The key is normalised (trimmed + lowercased) before lookup, matching the normalisation
    /// applied by <c>ConversationManagerActor</c> at handshake ingress.
    /// </summary>
    /// <param name="deliveryMode">The delivery-mode key declared by the channel.</param>
    /// <returns>The concrete <see cref="IChannelService"/> registered for this mode.</returns>
    /// <exception cref="InvalidOperationException">No implementation registered for this key.</exception>
    IChannelService Resolve(string deliveryMode);

    /// <summary>
    /// Returns <see langword="true"/> if a concrete <see cref="IChannelService"/> is registered
    /// for <paramref name="deliveryMode"/>. Used by the start-conversation gate to reject
    /// unknown delivery modes at the handshake with a 400 instead of letting the failure surface
    /// at send-time.
    /// </summary>
    /// <param name="deliveryMode">The delivery-mode key to check.</param>
    bool IsRegistered(string deliveryMode);
}

/// <summary>
/// Binding of a concrete <see cref="IChannelService"/> to the <c>deliveryMode</c> key it serves.
/// Each concrete channel implementation is registered in DI as one of these; the factory
/// collects them all via <c>IEnumerable&lt;ChannelServiceRegistration&gt;</c> and builds its
/// dispatch table.
/// </summary>
/// <param name="DeliveryMode">The delivery-mode key this service serves (e.g. <c>"signalr"</c>,
/// <c>"webhook"</c>). Normalised (trimmed + lowercased) by the factory.</param>
/// <param name="Service">The concrete channel service instance.</param>
public record ChannelServiceRegistration(string DeliveryMode, IChannelService Service);
