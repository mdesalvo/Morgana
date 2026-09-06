using System.Security.Cryptography;

namespace Morgana.AI.Services;

/// <summary>
/// The secret this installation signs consultations between its own agents with and proves them
/// against when they knock back at its own A2A door.
/// </summary>
/// <remarks>
/// A local consultation really does leave over HTTP and come back, so it needs a credential like any
/// caller — but that credential is minted here and read here and reaches nobody else, which is what
/// makes it nothing for a deployment to configure. It is coined fresh at every start: no token
/// outlives the turn that carries it, so nothing depends on the secret surviving a restart. An
/// installation is reached by its own agents at the address it bound, never through a load balancer,
/// so several instances each holding a different secret still each talk to themselves.
/// </remarks>
public sealed class PeerRingKeyService
{
    /// <summary>
    /// The secret itself, wide enough to satisfy the same 256-bit floor every declared channel and partner key is held to.
    /// </summary>
    public string SymmetricKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}