using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Morgana.Web.Filters;

/// <summary>
/// Admits to one published A2A agent only the partners its inbound policy reaches, fail-closed.
/// One instance is built per published agent at startup, so the scope it enforces is the one the startup checks validated.
/// </summary>
/// <param name="authenticationService">Validates the bearer token, exactly as the channels' gate does.</param>
/// <param name="publishedIntent">Agent this filter guards, named in the diagnostics.</param>
/// <param name="admittedIssuers">Partners whose inbound policy reaches this agent, plus this installation's own agents.</param>
/// <param name="logger">Records who was turned away and why: the caller only ever sees a bare 401.</param>
public sealed class PartnerAuthenticationFilter(
    IAuthenticationService authenticationService,
    string publishedIntent,
    HashSet<string> admittedIssuers,
    ILogger logger) : IEndpointFilter
{
    /// <summary>Scheme every caller must present, peers included.</summary>
    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// Where the proven identity of the caller is left for the rest of the request to read.
    /// The A2A hosting hands the session store a context id and never the request, so the store reads the caller here
    /// to decide whose conversation a request is served on, which keeps one partner out of another's.
    /// </summary>
    public const string CallerIssuerItemKey = "morgana.a2a.caller_issuer";

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // The partner's credential, as the A2A client of a Morgana or of anyone else sends it.
        string? authorization = context.HttpContext.Request.Headers.Authorization.FirstOrDefault();

        // A missing header or one that is not a bearer is turned away before the token is ever read: an
        // endpoint filter runs in front of the A2A handler, so what stops here never reaches an agent.
        if (authorization is null || !authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
            return Results.Unauthorized();

        // The same service the channels' gate calls, so a peer request is proven exactly as a channel's
        // is (signature against that issuer's own key, audience and lifetime): one gate cannot
        // quietly grow weaker than the other.
        Records.AuthenticationResult authentication =
            await authenticationService.AuthenticateAsync(authorization[BearerPrefix.Length..]);

        // Fail-closed on anything short of a proven token: a malformed, expired, wrongly signed or
        // undeclared-issuer token is a refusal, never a request served with less certainty.
        if (!authentication.IsAuthenticated)
            return Results.Unauthorized();

        // Authentic is not enough here: behind this filter a request reaches an agent's actor directly, with
        // none of the user's guard, the classifier and the channel rate limit a user's path goes through. Which agents a caller reaches
        // is drawn from the partner declarations alone, so a channel's key is turned away by the same test.
        if (authentication.Issuer is null || !admittedIssuers.Contains(authentication.Issuer))
        {
            logger.LogWarning(
                "A2A call to '{Intent}' refused: '{Issuer}' is not a partner admitted to it under Morgana:AgentToAgent:Partners",
                publishedIntent, authentication.Issuer);
            return Results.Unauthorized();
        }

        // Left for the session store, the only other party that has to know who this is and the only
        // one with no way to find out for itself.
        context.HttpContext.Items[CallerIssuerItemKey] = authentication.Issuer;

        // The A2A handler serves the request, the caller now proven and admitted.
        return await next(context);
    }
}