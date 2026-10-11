using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Cauldron.Services;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Cauldron.Handlers;

/// <summary>
/// HTTP message handler that self-issues short-lived JWT tokens for authenticating
/// Cauldron's REST API calls to the Morgana backend.
/// </summary>
/// <remarks>
/// <para><strong>Why self-issued tokens?</strong></para>
/// <para>Cauldron is a Blazor Server application — HTTP calls originate from the server process,
/// not the browser. The server can safely hold the shared symmetric key and generate tokens
/// without involving an external identity provider.</para>
///
/// <para><strong>Token characteristics:</strong></para>
/// <list type="bullet">
/// <item>Algorithm: HMAC-SHA256 with shared symmetric key</item>
/// <item>Issuer: <c>cauldron</c> (must be in Morgana's <c>ValidIssuers</c> list)</item>
/// <item>Subject: <c>cauldron-app</c> (identifies the official frontend)</item>
/// <item>Lifetime: 5 minutes (re-generated per request by the handler)</item>
/// </list>
/// </remarks>
public class MorganaAuthHandler : DelegatingHandler
{
    /// <summary>Key and algorithm that sign every token, shared with Morgana's entry for this channel.</summary>
    private readonly SigningCredentials _credentials;

    /// <summary>Issuer claim that Morgana looks up among its declared channels.</summary>
    private readonly string _issuer;

    /// <summary>Audience claim that Morgana requires on every token.</summary>
    private readonly string _audience;

    /// <summary>Serializes and signs the tokens.</summary>
    private readonly JsonWebTokenHandler _tokenHandler = new();

    /// <summary>
    /// Reads the signing key and the token claims from <c>Cauldron:Authentication</c>.
    /// </summary>
    public MorganaAuthHandler(IConfiguration configuration)
    {
        // Everything the handler needs sits in one configuration section
        IConfigurationSection authSection = configuration.GetSection("Cauldron:Authentication");

        // Without the key no token can be signed: startup fails here instead of on the first request
        string symmetricKey = authSection["SymmetricKey"]
            ?? throw new InvalidOperationException("Cauldron:Authentication:SymmetricKey is required for Cauldron authentication.");

        // The defaults match what Morgana expects from an unmodified channel entry
        _issuer = authSection["Issuer"] ?? "cauldron";
        _audience = authSection["Audience"] ?? "morgana.ai";

        // Signed with HMAC-SHA256 like Morgana's validator expects
        _credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)), SecurityAlgorithms.HmacSha256);
    }

    /// <summary>
    /// Signs the outgoing request with a fresh token before it reaches Morgana.
    /// </summary>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Minted per request rather than cached: tokens live five minutes and a long-lived
        // circuit would otherwise keep presenting an expired one.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateToken());
        // The request continues down the pipeline now that it carries its credentials
        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Generates a short-lived JWT token for Morgana API authentication.
    /// This method is also used by <see cref="SignalRService"/> for SignalR hub authentication.
    /// </summary>
    public string GenerateToken()
    {
        // Issuer must match an entry in Morgana's Authentication:Issuers list and the key must
        // match that entry's: an unknown issuer is rejected at the gate before any handler runs.
        SecurityTokenDescriptor descriptor =
            new SecurityTokenDescriptor
            {
                Issuer = _issuer,
                Audience = _audience,
                Subject = new ClaimsIdentity(
                [
                    new Claim(JwtRegisteredClaimNames.Sub, "cauldron-app"),
                    new Claim(JwtRegisteredClaimNames.Name, "Cauldron")
                ]),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = _credentials
            };

        // Signed and serialized: the compact JWT that goes into the Bearer header or the hub query
        return _tokenHandler.CreateToken(descriptor);
    }
}