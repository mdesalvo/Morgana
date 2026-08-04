using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="IAuthenticationService"/> implementation: validates JWTs signed with a
/// shared symmetric key (HMAC-SHA256) on a per-issuer basis. One <see cref="TokenValidationParameters"/>
/// bundle per declared issuer, each pinned to that issuer's own key — see
/// <see cref="AuthenticateAsync"/> for the peek-then-validate flow that selects the right one.
/// </summary>
public class JWTAuthenticationService : IAuthenticationService
{
    /// <summary>
    /// One validation bundle per declared issuer, each pinned to that issuer's own signing key —
    /// which is what keeps a leaked channel key from validating another channel's tokens. Built
    /// once at construction; an issuer absent from this map is rejected outright.
    /// </summary>
    private readonly Dictionary<string, TokenValidationParameters> validationParametersByIssuer;

    /// <summary>
    /// Stateless, thread-safe token reader/validator, shared across all calls.
    /// </summary>
    private readonly JsonWebTokenHandler jsonWebTokenHandler = new JsonWebTokenHandler();

    /// <summary>
    /// Logger for the issuers configured at startup and for each rejection — the caller only ever
    /// sees a fail-closed result, so the reason lives here.
    /// </summary>
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

            // HMAC-SHA256 needs a key at least as long as its output (256 bits / 32 bytes) to
            // deliver its full security margin — a shorter key is startup-fatal, not a warning,
            // because a weak signing key is silently exploitable, not silently degraded.
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
            // ReadJsonWebToken only decodes the JWT's own claims — it does NOT check the
            // signature. Peeking iss here is safe precisely because it's unauthenticated: it's
            // used only to pick which issuer's key to validate against, and ValidateTokenAsync
            // below is what actually proves the token was signed with that issuer's own key —
            // an attacker forging an iss claim still fails the signature check on the real key.
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
                // Maps the library's internal exception type to a stable, user/log-facing string —
                // deliberately not result.Exception.Message, which is free-form library prose that
                // could change across a NuGet update and isn't meant for external consumption.
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

            // "name" is optional on a channel's self-issued token; falling back to the "sub" value
            // (the user id itself) means callers always get a non-null DisplayName to show.
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
