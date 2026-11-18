namespace Morgana.Contracts;

/// <summary>The bearer token on every webhook POST, by which a channel tells Morgana's deliveries from anyone else's.</summary>
/// <remarks>It is signed with the channel's own SymmetricKey, names the channel as audience, lives five minutes and carries the hash of the exact body.</remarks>
public static class WebhookAuthentication
{
    /// <summary>The <c>iss</c> of every delivery token: the key is the channel's, the voice is Morgana's.</summary>
    public const string Issuer = "morgana";

    /// <summary>The claim carrying the SHA-256 of the POST body's exact bytes, base64url without padding, so a token vouches for one body and no other.</summary>
    /// <remarks>A channel hashes the bytes it received, never a re-serialization of what it parsed, since two encodings of one message differ.</remarks>
    public const string BodyHashClaim = "body_sha256";
}