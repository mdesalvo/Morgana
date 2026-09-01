using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Morgana.Web.Filters;

/// <summary>
/// Applies Morgana's own authentication to the A2A JSON-RPC endpoints: the same issuer whitelist,
/// the same audience, fail-closed, that <c>MorganaController</c> applies to every REST call — then
/// narrows it to the peer issuer alone.
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

        // No header, or one that is not a bearer, is turned away before the token is ever read: an
        // endpoint filter runs in front of the A2A handler, so what stops here never reaches an agent.
        if (authorization is null || !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();

        // The same service the controller calls, so a peer request is proven exactly as a channel's
        // is — signature against that issuer's own key, audience and lifetime — and one gate cannot
        // quietly grow weaker than the other.
        Records.AuthenticationResult authentication =
            await authenticationService.AuthenticateAsync(authorization[BearerPrefix.Length..]);

        // Fail-closed on anything short of a proven token: a malformed, expired, wrongly signed or
        // undeclared-issuer token is a refusal, never a request served with less certainty.
        if (!authentication.IsAuthenticated)
            return Results.Unauthorized();

        // Authentic is not enough here: behind this filter a request reaches an agent's actor
        // directly, with none of the guard, classifier, rate limit and dust budget a channel's own
        // path goes through. So only the issuer this installation signs peer traffic under is let
        // in — a channel's key opens the door it was cut for, not this one. Compared the way the
        // signing key is resolved, so the gate and the signer cannot disagree on who a peer is.
        return string.Equals(authentication.Issuer, Constants.AgentToAgent.IssuerName, StringComparison.OrdinalIgnoreCase)
            ? await next(context)
            : Results.Unauthorized();
    }
}
