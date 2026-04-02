using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IAuthenticationService"/> implementation that validates JWT tokens
/// signed with a shared symmetric key (HMAC-SHA256).
/// </summary>
/// <remarks>
/// <para><strong>Validation Strategy:</strong></para>
/// <list type="bullet">
/// <item>Signature: HMAC-SHA256 with a shared symmetric key from configuration</item>
/// <item>Issuer: must match one of the configured <c>ValidIssuers</c> (whitelist)</item>
/// <item>Audience: must match the configured <c>Audience</c></item>
/// <item>Lifetime: token must not be expired</item>
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
    private readonly TokenValidationParameters validationParameters;
    private readonly JsonWebTokenHandler jsonWebTokenHandler = new JsonWebTokenHandler();
    private readonly ILogger logger;

    /// <summary>
    /// Initialises a new instance of <see cref="JWTAuthenticationService"/>.
    /// Validates the shared symmetric key used for HMAC-SHA256 encryption.
    /// </summary>
    public JWTAuthenticationService(IOptions<Records.AuthenticationOptions> options, ILogger logger)
    {
        this.logger = logger;
        Records.AuthenticationOptions config = options.Value;

        #region SymmetricKey Validation
        if (string.IsNullOrWhiteSpace(config.SymmetricKey))
        {
            throw new InvalidOperationException(
                        "Morgana authentication is enabled but no SymmetricKey is configured. " +
                        "Set 'Morgana:Authentication:SymmetricKey' in appsettings.json or User Secrets.");
        }

        byte[] keyBytes = Encoding.UTF8.GetBytes(config.SymmetricKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                        "Morgana authentication SymmetricKey must be at least 256 bits (32 bytes). " +
                        $"Current key is {keyBytes.Length * 8} bits.");
        }
        #endregion

        validationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ValidateIssuer = true,
            ValidIssuers = config.ValidIssuers,
            ValidateAudience = true,
            ValidAudience = config.Audience,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };

        this.logger.LogInformation(
            "JWT authentication initialized — audience: {Audience}, valid issuers: [{Issuers}]",
            config.Audience, string.Join(", ", config.ValidIssuers));
    }

    /// <inheritdoc />
    public async Task<Records.AuthenticationResult> AuthenticateAsync(string token)
    {
        try
        {
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