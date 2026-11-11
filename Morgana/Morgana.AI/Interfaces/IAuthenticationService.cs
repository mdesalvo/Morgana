namespace Morgana.AI.Interfaces;

/// <summary>Proves who a bearer token belongs to: a channel, a partner or this installation's own ring.</summary>
// Fail-closed: any fault of the token is a result with IsAuthenticated false, never an exception.
// The gates extract the token and the service knows no transport, so API keys, mTLS or OAuth can replace it through DI.
public interface IAuthenticationService
{
    /// <summary>Validates a raw token without its <c>Bearer </c> prefix and reports the caller it proves.</summary>
    // Every request but the health probe passes here, so an implementation makes no external call on this path.
    Task<Records.AuthenticationResult> AuthenticateAsync(string token);
}