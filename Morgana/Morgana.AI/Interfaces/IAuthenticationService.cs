namespace Morgana.AI.Interfaces;

/// <summary>
/// Validates bearer tokens and extracts caller identity (fail-closed design: any validation error → IsAuthenticated=false).
/// JWTAuthenticationService: HMAC-SHA256 tokens, per-issuer symmetric keys, checks issuer/audience/expiry. Swappable via DI
/// for API keys, mTLS, OAuth. Controller extracts token; service is transport-agnostic.
/// </summary>
public interface IAuthenticationService
{
    /// <summary>
    /// Validates bearer token and extracts caller identity (UserId, DisplayName). Token is raw value only
    /// (no "Bearer " prefix). Must be low-latency (no external calls on hot path). Returns AuthenticationResult.
    /// </summary>
    Task<Records.AuthenticationResult> AuthenticateAsync(string token);
}
