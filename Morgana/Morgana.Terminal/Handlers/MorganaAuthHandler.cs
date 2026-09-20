using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Morgana.Terminal.Handlers;

/// <summary>
/// HTTP message handler that self-issues short-lived JWT tokens for authenticating a channel's
/// REST API calls to the Morgana backend.
/// </summary>
/// <remarks>
/// <para><strong>Token characteristics:</strong></para>
/// <list type="bullet">
/// <item>Algorithm: HMAC-SHA256 with shared symmetric key</item>
/// <item>Issuer: the channel name (must be present in Morgana's <c>Authentication:Issuers[]</c> list
/// with a matching <c>SymmetricKey</c>; unknown issuers are rejected at the gate)</item>
/// <item>Subject: <c>&lt;channel&gt;-app</c></item>
/// <item>Lifetime: 5 minutes, regenerated per request</item>
/// </list>
/// </remarks>
public class MorganaAuthHandler : DelegatingHandler
{
    /// <summary>HMAC-SHA256 signing credentials derived from the configured symmetric key.</summary>
    private readonly SigningCredentials credentials;

    /// <summary>JWT <c>iss</c> claim; must match an entry in Morgana's <c>Authentication:Issuers[]</c>.</summary>
    private readonly string issuer;

    /// <summary>JWT <c>aud</c> claim expected by Morgana's token validation.</summary>
    private readonly string audience;

    /// <summary>The channel the token speaks for, carried into the subject and name claims.</summary>
    private readonly ChannelProfile profile;

    /// <summary>Reusable token writer; <see cref="JsonWebTokenHandler"/> is thread-safe.</summary>
    private readonly JsonWebTokenHandler tokenHandler = new();

    /// <summary>
    /// Reads the signing key, issuer and audience from the channel's <c>Authentication</c> configuration section.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the channel's <c>Authentication:SymmetricKey</c> is missing.</exception>
    public MorganaAuthHandler(IConfiguration configuration, ChannelProfile profile)
    {
        this.profile = profile;
        IConfigurationSection authSection = configuration.GetSection(profile.SectionKey("Authentication"));
        string symmetricKey = authSection["SymmetricKey"]
            ?? throw new InvalidOperationException($"{profile.SectionKey("Authentication")}:SymmetricKey is required for {profile.DisplayName} authentication.");
        issuer = authSection["Issuer"] ?? profile.ChannelName;
        audience = authSection["Audience"] ?? "morgana.ai";
        credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)), SecurityAlgorithms.HmacSha256);
    }

    /// <summary>Attaches a freshly-issued Bearer token to the outgoing request before forwarding it down the pipeline.</summary>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateToken());
        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>Generates a new HMAC-SHA256 signed JWT valid for 5 minutes.</summary>
    public string GenerateToken()
    {
        SecurityTokenDescriptor descriptor =
            new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Subject = new ClaimsIdentity(
                [
                    new Claim(JwtRegisteredClaimNames.Sub, $"{profile.ChannelName}-app"),
                    new Claim(JwtRegisteredClaimNames.Name, profile.DisplayName)
                ]),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = credentials
            };

        return tokenHandler.CreateToken(descriptor);
    }
}
