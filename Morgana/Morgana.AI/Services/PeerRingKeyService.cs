using System.Security.Cryptography;

namespace Morgana.AI.Services;

/// <summary>
/// The secret that this installation signs its own agents' consultations with and proves them against at its own A2A door.
/// A local consultation leaves over HTTP and comes back, so it needs a credential like any caller: minted and read here alone,
/// it is nothing for a deployment to configure. Coined at every start, since no token outlives the turn that carries it;
/// several instances each holding their own secret still each talk to themselves, being reached at the address they bound.
/// </summary>
public sealed class PeerRingKeyService
{
    /// <summary>The secret itself, wide enough for the 256-bit floor that every channel and partner key is held to.</summary>
    public string SymmetricKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}