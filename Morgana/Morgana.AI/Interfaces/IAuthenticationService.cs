namespace Morgana.AI.Interfaces;

/// <summary>
/// Validates a bearer token and says who the caller is. It fails closed: any validation error yields an unauthenticated
/// result and never an exception. It knows nothing of the transport that carried the token.
/// </summary>
public interface IAuthenticationService
{
    /// <summary>
    /// Validates the token and returns the caller's identity. The token is the raw value without the "Bearer " prefix.
    /// It runs on the path of every request, so an implementation must not make external calls.
    /// </summary>
    Task<Records.AuthenticationResult> AuthenticateAsync(string token);
}
