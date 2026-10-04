using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Morgana.AI;
using Morgana.AI.Interfaces;

namespace Morgana.Web.Filters;

/// <summary>Admits a REST call only from a channel carrying a valid bearer token, fail-closed.</summary>
/// <param name="authenticationService">Validates the token against its own issuer's key.</param>
/// <param name="logger">Records who was turned away and why.</param>
public sealed class ChannelAuthenticationFilter(
    IAuthenticationService authenticationService,
    ILogger logger) : IAsyncAuthorizationFilter
{
    /// <summary>Scheme every channel must present.</summary>
    private const string BearerPrefix = "Bearer ";

    /// <summary>Where the authenticated caller is left for the action, which sends the message under it.</summary>
    public const string CallerIdItemKey = "morgana.channel.caller_id";

    /// <inheritdoc />
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // The liveness probe is asked by infrastructure holding no channel key
        if (context.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Any())
            return;

        // A call carrying no bearer is refused before any token is read: fail-closed, nothing is served on less
        string authorizationHeader = context.HttpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorizationHeader) || !authorizationHeader.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Authentication failed: missing or malformed Authorization header");
            context.Result = new UnauthorizedObjectResult(new { error = "Missing or malformed Authorization header. Expected: Bearer <token>" });
            return;
        }

        // Proven against the key of the issuer the token names, so one leaked channel key opens no other channel
        Records.AuthenticationResult authResult = await authenticationService.AuthenticateAsync(authorizationHeader[BearerPrefix.Length..].Trim());
        if (!authResult.IsAuthenticated)
        {
            logger.LogWarning("Authentication failed: {Error}", authResult.Error);
            context.Result = new UnauthorizedObjectResult(new { error = authResult.Error });
            return;
        }

        // A partner's inbound policy may hold it to a few agents over A2A: a conversation here would
        // hand it every agent back through the classifier
        if (authResult.IsPartner)
        {
            logger.LogWarning("Authentication rejected: issuer '{Issuer}' is a partner and the conversation API serves channels", authResult.Issuer);
            context.Result = new UnauthorizedObjectResult(new { error = "This API serves channels; a partner consults published agents over A2A." });
            return;
        }

        // The identity a message is sent under comes from the proven token, never from anything the body claims
        context.HttpContext.Items[CallerIdItemKey] = authResult.CallerId;
    }
}