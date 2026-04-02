using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
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
    private readonly SigningCredentials _credentials;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly JsonWebTokenHandler _tokenHandler = new();

    public MorganaAuthHandler(IConfiguration configuration)
    {
        IConfigurationSection authSection = configuration.GetSection("Cauldron:Authentication");
        string symmetricKey = authSection["SymmetricKey"]
            ?? throw new InvalidOperationException("Cauldron:Authentication:SymmetricKey is required for Cauldron authentication.");
        _issuer = authSection["Issuer"] ?? "cauldron";
        _audience = authSection["Audience"] ?? "morgana.ai";
        _credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)), SecurityAlgorithms.HmacSha256);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateToken());
        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Generates a short-lived JWT token for Morgana API authentication.
    /// This method is also used by <see cref="Services.MorganaSignalRService"/> for SignalR hub authentication.
    /// </summary>
    public string GenerateToken()
    {
        SecurityTokenDescriptor descriptor =
            new SecurityTokenDescriptor
            {
                Issuer = _issuer,
                Audience = _audience,
                Subject = new ClaimsIdentity(
                [
                    new Claim(JwtRegisteredClaimNames.Sub, "cauldron-app"),
                    new Claim("name", "Cauldron")
                ]),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = _credentials
            };

        return _tokenHandler.CreateToken(descriptor);
    }
}