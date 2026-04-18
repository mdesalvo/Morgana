using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IAuthenticationService"/> implementation that validates JWT tokens
/// signed with a shared symmetric key (HMAC-SHA256) on a per-issuer basis.
/// </summary>
/// <remarks>
/// <para><strong>Per-Issuer Trust Model:</strong></para>
/// <para>One <see cref="TokenValidationParameters"/> bundle is built per declared issuer,
/// each pinned to that issuer's own signing key. On validation the <c>iss</c> claim is
/// peeked first (without trusting the token), then the matching bundle is selected.
/// Tokens whose <c>iss</c> is not declared in configuration are rejected outright,
/// so the blast radius of a leaked key is limited to a single channel.</para>
///
/// <para><strong>Validation Strategy:</strong></para>
/// <list type="bullet">
/// <item>Signature: HMAC-SHA256 with the issuer's own shared symmetric key</item>
/// <item>Issuer: must be declared in <c>Morgana:Authentication:Issuers</c></item>
/// <item>Audience: must match the configured <c>Audience</c></item>
/// <item>Lifetime: token must not be expired (30s clock skew)</item>
/// </list>
///
/// <para><strong>Identity Extraction:</strong></para>
/// <para>On successful validation, extracts <c>sub</c> → UserId and <c>name</c> → DisplayName
/// from the token claims. If <c>name</c> is absent, falls back to the <c>sub</c> value.</para>
///
/// <para><strong>No External Dependencies:</strong></para>
/// <para>All validation is performed locally using <c>Microsoft.IdentityModel.JsonWebTokens</c>.
/// No network calls, no IdP dependency.</para>
/// </remarks>
public class JWTAuthenticationService : IAuthenticationService
{
    private readonly Dictionary<string, TokenValidationParameters> validationParametersByIssuer;
    private readonly JsonWebTokenHandler jsonWebTokenHandler = new JsonWebTokenHandler();
    private readonly ILogger logger;

    /// <summary>
    /// Initialises a new instance of <see cref="JWTAuthenticationService"/>.
    /// Builds one validation bundle per declared issuer and validates each issuer's
    /// signing key length (HMAC-SHA256 requires at least 256 bits).
    /// </summary>
    public JWTAuthenticationService(IOptions<Records.AuthenticationOptions> options, ILogger logger)
    {
        this.logger = logger;
        Records.AuthenticationOptions config = options.Value;

        #region Issuers Validation
        if (config.Issuers is null || config.Issuers.Count == 0)
        {
            throw new InvalidOperationException(
                        "Morgana authentication requires at least one issuer. " +
                        "Declare entries under 'Morgana:Authentication:Issuers' in appsettings.json or User Secrets.");
        }
        #endregion

        validationParametersByIssuer = new Dictionary<string, TokenValidationParameters>(StringComparer.Ordinal);

        foreach (Records.IssuerOptions issuer in config.Issuers)
        {
            #region Issuer Validation
            if (string.IsNullOrWhiteSpace(issuer.Name))
            {
                throw new InvalidOperationException(
                            "Morgana authentication issuer entry is missing 'Name'.");
            }

            if (string.IsNullOrWhiteSpace(issuer.SymmetricKey))
            {
                throw new InvalidOperationException(
                            $"Morgana authentication issuer '{issuer.Name}' has no SymmetricKey configured.");
            }

            byte[] keyBytes = Encoding.UTF8.GetBytes(issuer.SymmetricKey);
            if (keyBytes.Length < 32)
            {
                throw new InvalidOperationException(
                            $"Morgana authentication SymmetricKey for issuer '{issuer.Name}' must be at least 256 bits (32 bytes). " +
                            $"Current key is {keyBytes.Length * 8} bits.");
            }

            if (validationParametersByIssuer.ContainsKey(issuer.Name))
            {
                throw new InvalidOperationException(
                            $"Morgana authentication issuer '{issuer.Name}' is declared more than once.");
            }
            #endregion

            validationParametersByIssuer[issuer.Name] = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                ValidateIssuer = true,
                ValidIssuer = issuer.Name,
                ValidateAudience = true,
                ValidAudience = config.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        }

        this.logger.LogInformation(
            "JWT authentication initialized — audience: {Audience}, issuers: [{Issuers}]",
            config.Audience, string.Join(", ", validationParametersByIssuer.Keys));
    }

    /// <inheritdoc />
    public async Task<Records.AuthenticationResult> AuthenticateAsync(string token)
    {
        try
        {
            #region Issuer Lookup
            // Peek the iss claim WITHOUT trusting the token. This selects which key to
            // validate against; the signature check below proves the token actually
            // belongs to that issuer.
            string? issuer;
            try
            {
                issuer = jsonWebTokenHandler.ReadJsonWebToken(token)?.Issuer;
            }
            catch
            {
                logger.LogWarning("JWT rejected: token is malformed");
                return new Records.AuthenticationResult(IsAuthenticated: false, Error: "Token is malformed");
            }

            if (string.IsNullOrEmpty(issuer))
            {
                logger.LogWarning("JWT rejected: token has no 'iss' claim");
                return new Records.AuthenticationResult(IsAuthenticated: false, Error: "Token has no 'iss' claim");
            }

            if (!validationParametersByIssuer.TryGetValue(issuer, out TokenValidationParameters? validationParameters))
            {
                logger.LogWarning("JWT rejected: issuer '{Issuer}' is not declared", issuer);
                return new Records.AuthenticationResult(IsAuthenticated: false, Error: "Token issuer is not in the list of valid issuers");
            }
            #endregion

            TokenValidationResult result = await jsonWebTokenHandler.ValidateTokenAsync(token, validationParameters);

            #region Validation
            if (!result.IsValid)
            {
                string error = result.Exception switch
                {
                    SecurityTokenExpiredException => "Token has expired",
                    SecurityTokenInvalidIssuerException => "Token issuer is not in the list of valid issuers",
                    SecurityTokenInvalidAudienceException => "Token audience does not match expected value",
                    SecurityTokenInvalidSignatureException => "Token signature is invalid",
                    _ => "Token validation failed"
                };

                logger.LogWarning("JWT rejected: {Error}", error);
                return new Records.AuthenticationResult(IsAuthenticated: false, Error: error);
            }

            string? userId = result.Claims.TryGetValue(JwtRegisteredClaimNames.Sub, out object? subValue) ? subValue?.ToString() : null;
            if (string.IsNullOrEmpty(userId))
            {
                logger.LogWarning("JWT valid but missing 'sub' claim");
                return new Records.AuthenticationResult(IsAuthenticated: false, Error: "Token is valid but missing required 'sub' claim");
            }
            #endregion

            string? displayName = result.Claims.TryGetValue(JwtRegisteredClaimNames.Name, out object? nameValue) ? nameValue?.ToString() : userId;
            return new Records.AuthenticationResult(IsAuthenticated: true, UserId: userId, DisplayName: displayName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "JWT rejected: validation failed");
            return new Records.AuthenticationResult(IsAuthenticated: false, Error: "Token validation failed");
        }
    }
}
