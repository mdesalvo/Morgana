using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Morgana.Web.Filters;

/// <summary>
/// Applies Morgana's own authentication to one published A2A endpoint: the same issuer whitelist,
/// the same audience, fail-closed, that <c>MorganaController</c> applies to every REST call — then
/// narrows it to the systems admitted to <em>this</em> agent.
/// </summary>
/// <remarks>
/// Built once per published agent rather than resolved per request, so the scope it enforces is
/// settled at startup beside the checks that validate it — exactly as the issuers themselves are
/// baked once into <c>IAuthenticationService</c>.
/// </remarks>
/// <param name="authenticationService">Validates the bearer token, exactly as the controller does.</param>
/// <param name="publishedIntent">Agent this filter guards, named in the diagnostics.</param>
/// <param name="admittedIssuers">Issuers whose inbound declaration reaches this agent.</param>
/// <param name="logger">Records who was turned away and why: the caller only ever sees a bare 401.</param>
public sealed class A2AAuthenticationFilter(
    IAuthenticationService authenticationService,
    string publishedIntent,
    HashSet<string> admittedIssuers,
    ILogger logger) : IEndpointFilter
{
    /// <summary>Scheme every caller must present, peers included.</summary>
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Where the proven identity of the caller is left for the rest of the request to read.
    /// </summary>
    /// <remarks>
    /// The A2A hosting layer hands an agent a session built from the context id and never the
    /// request, so the one party that knows who is calling is this gate. The session store reads it
    /// back to decide whose conversation an inbound request is served on, which is what keeps one
    /// caller out of another's.
    /// </remarks>
    public const string CallerIssuerItemKey = "morgana.a2a.caller_issuer";

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
        // path goes through. A caller is a channel or a colleague, never both, so a channel's key
        // opens the door it was cut for and not this one.
        if (authentication.IssuerType is not Records.IssuerType.System)
        {
            logger.LogWarning(
                "A2A call to '{Intent}' refused: issuer '{Issuer}' is declared as a channel, not a system",
                publishedIntent, authentication.Issuer);
            return Results.Unauthorized();
        }

        // Proven to be a colleague and now: which desks. An issuer admitted to the installation is
        // not thereby admitted to every agent of it — that is what makes this installation openable
        // to a partner one desk at a time.
        if (authentication.Issuer is null || !admittedIssuers.Contains(authentication.Issuer))
        {
            logger.LogWarning(
                "A2A call to '{Intent}' refused: system '{Issuer}' is not admitted to it under Morgana:AgentToAgent:InboundSystems",
                publishedIntent, authentication.Issuer);
            return Results.Unauthorized();
        }

        // Left for the session store, the only other party that has to know who this is and the only
        // one with no way to find out for itself.
        context.HttpContext.Items[CallerIssuerItemKey] = authentication.Issuer;

        return await next(context);
    }
}
