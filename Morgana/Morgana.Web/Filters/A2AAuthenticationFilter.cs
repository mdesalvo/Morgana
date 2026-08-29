using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Morgana.Web.Filters;

/// <summary>
/// Applies Morgana's own authentication to the A2A JSON-RPC endpoints: the same issuer whitelist,
/// the same audience, fail-closed, that <c>MorganaController</c> applies to every REST call.
/// </summary>
/// <param name="authenticationService">Validates the bearer token, exactly as the controller does.</param>
public sealed class A2AAuthenticationFilter(IAuthenticationService authenticationService) : IEndpointFilter
{
    /// <summary>Scheme every caller must present, peers included.</summary>
    private const string BearerPrefix = "Bearer ";

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        string? authorization = context.HttpContext.Request.Headers.Authorization.FirstOrDefault();

        if (authorization is null || !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();

        Records.AuthenticationResult authentication =
            await authenticationService.AuthenticateAsync(authorization[BearerPrefix.Length..]);

        return authentication.IsAuthenticated ? await next(context) : Results.Unauthorized();
    }
}
