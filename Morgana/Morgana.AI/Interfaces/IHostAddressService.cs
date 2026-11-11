namespace Morgana.AI.Interfaces;

/// <summary>Tells Morgana the address it answers on, so an A2A card names a callable endpoint with nothing configured.</summary>
// An extension point because Morgana.AI has no web host of its own and only the host can answer.
public interface IHostAddressService
{
    /// <summary>The base address of this installation without a trailing separator; null until the server has bound.</summary>
    string? ResolveBaseAddress();
}