using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Rune.Handlers;

/// <summary>
/// HTTP message handler that self-issues short-lived JWT tokens for authenticating
/// Rune's REST API calls to the Morgana backend.
/// </summary>
/// <remarks>
/// <para><strong>Token characteristics:</strong></para>
/// <list type="bullet">
/// <item>Algorithm: HMAC-SHA256 with shared symmetric key</item>
/// <item>Issuer: <c>rune</c> (must be present in Morgana's <c>Authentication:Issuers[]</c> list
/// with a matching <c>SymmetricKey</c>; unknown issuers are rejected at the gate)</item>
/// <item>Subject: <c>rune-app</c></item>
/// <item>Lifetime: 5 minutes, regenerated per request</item>
/// </list>
/// </remarks>
public class MorganaAuthHandler : DelegatingHandler
{
    private readonly SigningCredentials credentials;
    private readonly string issuer;
    private readonly string audience;
    private readonly JsonWebTokenHandler tokenHandler = new();

    public MorganaAuthHandler(IConfiguration configuration)
    {
        IConfigurationSection authSection = configuration.GetSection("Rune:Authentication");
        string symmetricKey = authSection["SymmetricKey"]
            ?? throw new InvalidOperationException("Rune:Authentication:SymmetricKey is required for Rune authentication.");
        issuer = authSection["Issuer"] ?? "rune";
        audience = authSection["Audience"] ?? "morgana.ai";
        credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)), SecurityAlgorithms.HmacSha256);
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateToken());
        return base.SendAsync(request, cancellationToken);
    }

    public string GenerateToken()
    {
        SecurityTokenDescriptor descriptor =
            new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Subject = new ClaimsIdentity(
                [
                    new Claim(JwtRegisteredClaimNames.Sub, "rune-app"),
                    new Claim(JwtRegisteredClaimNames.Name, "Rune")
                ]),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = credentials
            };

        return tokenHandler.CreateToken(descriptor);
    }
}
