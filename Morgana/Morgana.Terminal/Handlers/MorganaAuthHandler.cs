using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Morgana.Terminal.Handlers;

/// <summary>Signs every REST call of the channel to Morgana with a token it issues itself, HMAC-SHA256 with five minutes of life.</summary>
// The issuer is the channel's name, which Morgana's Authentication:Issuers must list with the same SymmetricKey: an unknown
// issuer is refused at the gate.
public class MorganaAuthHandler : DelegatingHandler
{
    /// <summary>The HMAC-SHA256 signing credentials derived from the configured symmetric key.</summary>
    private readonly SigningCredentials credentials;

    /// <summary>The token's <c>iss</c> claim, which must match an entry of Morgana's <c>Authentication:Issuers</c>.</summary>
    private readonly string issuer;

    /// <summary>The token's <c>aud</c> claim, which Morgana's validation expects.</summary>
    private readonly string audience;

    /// <summary>The channel the token speaks for, carried into the subject and name claims.</summary>
    private readonly ChannelProfile profile;

    /// <summary>Writes the tokens, shared by every call.</summary>
    private readonly JsonWebTokenHandler tokenHandler = new();

    /// <summary>Reads the signing key, issuer and audience from the channel's <c>Authentication</c> section.</summary>
    /// <exception cref="InvalidOperationException">The channel's <c>Authentication:SymmetricKey</c> is missing.</exception>
    public MorganaAuthHandler(IConfiguration configuration, ChannelProfile profile)
    {
        // The channel's identity goes into the claims of every token.
        this.profile = profile;

        // The key is the one secret the channel shares with Morgana, so a channel without it cannot sign anything.
        IConfigurationSection authenticationSection = configuration.GetSection(profile.SectionKey("Authentication"));
        string symmetricKey = authenticationSection["SymmetricKey"]
            ?? throw new InvalidOperationException($"{profile.SectionKey("Authentication")}:SymmetricKey is required for {profile.DisplayName} authentication.");

        // Issuer and audience default to the channel's name and to Morgana's own audience.
        issuer = authenticationSection["Issuer"] ?? profile.ChannelName;
        audience = authenticationSection["Audience"] ?? "morgana.ai";

        // The credentials are derived once and serve every token.
        credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(symmetricKey)), SecurityAlgorithms.HmacSha256);
    }

    /// <summary>Attaches a freshly issued bearer token to the outgoing request before forwarding it.</summary>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Every call carries a token issued at that moment, so it can never have expired in flight.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GenerateToken());
        return base.SendAsync(request, cancellationToken);
    }

    /// <summary>A new token signed with HMAC-SHA256 and valid for five minutes.</summary>
    private string GenerateToken()
    {
        // The subject names the channel's application and the name claim the channel as the user sees it.
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

        // The compact serialized token, ready for the Authorization header.
        return tokenHandler.CreateToken(descriptor);
    }
}