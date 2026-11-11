using Morgana.Contracts;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Resolves the transport registered under the <c>deliveryMode</c> that a channel declared at the handshake.
/// Dispatch follows DeliveryMode and never the free-form ChannelName, which the client controls and could name anything.
/// The valid keys are what DI registers rather than an enum, so a new transport is one registration and no contract moves.
/// </summary>
public interface IChannelServiceFactory
{
    /// <summary>
    /// The transport registered under <paramref name="deliveryMode"/>, matched after trimming and lowercasing.
    /// The start-conversation gate refuses an unregistered key with 400, so reaching this throw at send time is a broken invariant.
    /// </summary>
    /// <exception cref="InvalidOperationException">No transport is registered under the key.</exception>
    IChannelService Resolve(string deliveryMode);

    /// <summary>Tells whether a transport is registered under <paramref name="deliveryMode"/>, so the start-conversation gate refuses the others with 400.</summary>
    bool IsRegistered(string deliveryMode);
}

/// <summary>Binds a transport to the <c>deliveryMode</c> key it serves, one registration in DI for each transport.</summary>
/// <param name="DeliveryMode">The key the transport serves, such as <c>signalr</c> or <c>webhook</c>, trimmed and lowercased by the factory.</param>
/// <param name="Service">The transport that serves the key.</param>
public record ChannelServiceRegistration(string DeliveryMode, IChannelService Service);