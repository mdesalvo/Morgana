namespace Morgana.AI.Interfaces;

/// <summary>
/// Tells Morgana the address it answers on, so an A2A card can name a callable endpoint without
/// anyone configuring the application's own URL.
/// </summary>
/// <remarks>
/// An extension point because Morgana.AI has no web host of its own: only the host can answer.
/// </remarks>
public interface IHostAddressService
{
    /// <summary>
    /// Base address this instance is reachable at, without trailing separator; <c>null</c> while
    /// not yet knowable, typically before the server has bound.
    /// </summary>
    string? ResolveBaseAddress();
}