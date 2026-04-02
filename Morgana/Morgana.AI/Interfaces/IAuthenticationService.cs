namespace Morgana.AI.Interfaces;

/// <summary>
/// Service abstraction for authenticating incoming requests to the Morgana API.
/// Implementations validate bearer tokens and extract caller identity information.
/// </summary>
/// <remarks>
/// <para><strong>Design Intent:</strong></para>
/// <para>Decouples authentication logic from the HTTP pipeline. The controller extracts
/// the raw token from the <c>Authorization</c> header and delegates validation entirely
/// to this service, which is agnostic of transport details.</para>
///
/// <para><strong>Default Implementation:</strong></para>
/// <para><see cref="Services.JWTAuthenticationService"/> validates JWT tokens signed with
/// a shared symmetric key (HMAC-SHA256), checking issuer, audience, and expiry.
/// Swap it in DI to adopt any alternative authentication backend (API keys, mTLS,
/// OAuth with external IdP) without touching the controller or actor system.</para>
///
/// <para><strong>Fail-Closed Contract:</strong></para>
/// <para>Unlike guard rails and classifiers (which fail open), authentication must
/// fail closed: if the service cannot validate the token for any reason, it must
/// return <c>IsAuthenticated = false</c>. Only a positively validated token yields access.</para>
///
/// <para><strong>Configuration Example:</strong></para>
/// <code>
/// // appsettings.json
/// {
///   "Morgana": {
///     "Authentication": {
///       "Enabled": true,
///       "SymmetricKey": "your-256-bit-secret-key-here",
///       "ValidIssuers": ["morgana", "cauldron"],
///       "Audience": "morganaa.ai"
///     }
///   }
/// }
/// </code>
/// </remarks>
public interface IAuthenticationService
{
    /// <summary>
    /// Validates the provided bearer token and extracts the caller's identity.
    /// </summary>
    /// <param name="token">
    /// The raw bearer token extracted from the HTTP <c>Authorization</c> header.
    /// Implementations should not expect the <c>Bearer </c> prefix — only the token value.
    /// </param>
    /// <returns>
    /// An <see cref="Records.AuthenticationResult"/> indicating whether the token is valid
    /// and, when valid, the caller's identity (user ID and display name).
    /// </returns>
    /// <remarks>
    /// <para>This method is called for <strong>every</strong> authenticated request,
    /// so implementations should be designed for low latency (avoid external calls
    /// on the hot path — prefer local token validation).</para>
    /// </remarks>
    Task<Records.AuthenticationResult> AuthenticateAsync(string token);
}
