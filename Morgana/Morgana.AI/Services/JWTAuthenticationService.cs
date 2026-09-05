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
    /// Role each declared issuer was given, travelling back on a successful result. Kept here rather
    /// than re-read at every gate: the issuers are baked once at construction and two gates reading
    /// the same list separately could drift apart on what a caller is.
    /// </summary>
    private readonly Dictionary<string, Records.IssuerType> issuerTypesByName;

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
    /// Initialises the service over the declared issuers, refusing any declaration a caller could never
    /// be proven against.
    /// </summary>
    /// <remarks>
    /// Every check here is startup-fatal. What is accepted decides which door a key opens, so a defect
    /// found later is a caller wrongly admitted rather than one wrongly refused.
    /// </remarks>
    /// <param name="options">The <c>Morgana:Authentication</c> section.</param>
    /// <param name="logger">Logger for the issuers configured here and for every later rejection.</param>
    /// <exception cref="InvalidOperationException">A declaration is missing, weak or duplicated.</exception>
    public JWTAuthenticationService(IOptions<Records.AuthenticationOptions> options, ILogger logger)
    {
        this.logger = logger;
        Records.AuthenticationOptions config = options.Value;

        // With no issuer at all nobody can prove who they are, so this installation would answer nobody.
        if (config.Issuers is null || config.Issuers.Count == 0)
        {
            throw new InvalidOperationException(
                        "Morgana authentication requires at least one issuer. " +
                        "Declare entries under 'Morgana:Authentication:Issuers' in appsettings.json or User Secrets.");
        }
        
        validationParametersByIssuer = new Dictionary<string, TokenValidationParameters>(StringComparer.Ordinal);
        issuerTypesByName = new Dictionary<string, Records.IssuerType>(StringComparer.Ordinal);
        foreach (Records.IssuerOptions configuredIssuer in config.Issuers)
        {
            // Nothing is admitted before it is proven usable. The names accepted so far travel with the
            // entry, so a duplicate is weighed against what actually got in rather than against what
            // somebody wrote down.
            byte[] signingKey = ValidateIssuerDeclaration(configuredIssuer, validationParametersByIssuer.Keys);

            // The entry that admits this caller at all: from here a token naming this issuer is proven
            // against this key alone, never against another's.
            validationParametersByIssuer[configuredIssuer.Name] =
                new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(signingKey),
                    ValidateIssuer = true,
                    ValidIssuer = configuredIssuer.Name,
                    ValidateAudience = true,
                    ValidAudience = config.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };

            // The role beside the identity: which of the two doors this key opens is settled once, here,
            // rather than inferred at each gate from what the caller is trying to reach.
            issuerTypesByName[configuredIssuer.Name] = configuredIssuer.Type!.Value;
        }

        // The whole trust surface of this installation on one line, which is what an operator reads back
        // to see that a channel was onboarded as a channel rather than as a peer.
        this.logger.LogInformation(
            "JWT authentication initialized — audience: {Audience}, issuers: [{Issuers}]",
            config.Audience,
            string.Join(", ", issuerTypesByName.Select(declared => $"{declared.Key} ({declared.Value.ToString().ToLowerInvariant()})")));
    }

    /// <summary>
    /// Refuses an issuer declaration that could never prove a caller. Hands back its signing key.
    /// </summary>
    /// <param name="issuer">One entry under <c>Morgana:Authentication:Issuers</c>.</param>
    /// <param name="alreadyDeclared">Names accepted so far, against which this one must be new.</param>
    /// <returns>The signing key, proven to be at least 256 bits.</returns>
    /// <exception cref="InvalidOperationException">The declaration is incomplete, weak or duplicated.</exception>
    private static byte[] ValidateIssuerDeclaration(Records.IssuerOptions issuer, ICollection<string> alreadyDeclared)
    {
        // Nameless, so there is nothing for an iss claim to match.
        if (string.IsNullOrWhiteSpace(issuer.Name))
        {
            throw new InvalidOperationException(
                        "Morgana authentication issuer entry is missing 'Name'.");
        }

        // Keyless, so nothing this issuer signs could ever be proven: it would be refused at every door.
        if (string.IsNullOrWhiteSpace(issuer.SymmetricKey))
        {
            throw new InvalidOperationException(
                        $"Morgana authentication issuer '{issuer.Name}' has no SymmetricKey configured.");
        }

        // A caller is a channel or a colleague, never both. The two doors are not equally guarded, so the
        // role is declared rather than inferred: the option is nullable precisely so absence is
        // distinguishable, where a default would classify a caller by enum ordering.
        if (issuer.Type is null)
        {
            throw new InvalidOperationException(
                        $"Morgana authentication issuer '{issuer.Name}' declares no Type. "
                        + "Every entry must declare one: 'channel' for a client carrying users (REST and SignalR), "
                        + "'system' for a peer consulting this installation's agents over A2A.");
        }

        // HMAC-SHA256 needs a key at least as long as its output to deliver its full security margin.
        // Startup-fatal, not a warning: a weak signing key is exploitable rather than degraded.
        byte[] keyBytes = Encoding.UTF8.GetBytes(issuer.SymmetricKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                        $"Morgana authentication SymmetricKey for issuer '{issuer.Name}' must be at least 256 bits (32 bytes). " +
                        $"Current key is {keyBytes.Length * 8} bits.");
        }

        // One name declared twice leaves one of the two keys silently unusable. Which one survives
        // depends on the order somebody happened to write them in.
        if (alreadyDeclared.Contains(issuer.Name))
        {
            throw new InvalidOperationException(
                        $"Morgana authentication issuer '{issuer.Name}' is declared more than once.");
        }

        return keyBytes;
    }

    /// <inheritdoc />
    public async Task<Records.AuthenticationResult> AuthenticateAsync(string token)
    {
        try
        {
            // Who the token says issued it, read without being believed.
            string? issuer;
            try
            {
                issuer = jsonWebTokenHandler.ReadJsonWebToken(token)?.Issuer;
            }
            catch
            {
                // Not a token at all. Kept apart from a token that fails its checks, since the two send
                // whoever is debugging to opposite ends of the deployment.
                logger.LogWarning("JWT rejected: token is malformed");
                return new Records.AuthenticationResult(IsAuthenticated: false, Error: "Token is malformed");
            }

            // Naming nobody, so there is no key to check it against.
            if (string.IsNullOrEmpty(issuer))
            {
                logger.LogWarning("JWT rejected: token has no 'iss' claim");
                return new Records.AuthenticationResult(IsAuthenticated: false, Error: "Token has no 'iss' claim");
            }

            // An undeclared issuer is turned away before any key is touched: onboarding a caller is a
            // deliberate entry in configuration, never something a token asserts for itself.
            if (!validationParametersByIssuer.TryGetValue(issuer, out TokenValidationParameters? validationParameters))
            {
                logger.LogWarning("JWT rejected: issuer '{Issuer}' is not declared", issuer);
                return new Records.AuthenticationResult(IsAuthenticated: false, Error: "Token issuer is not in the list of valid issuers");
            }

            // The one moment the token is proven: signature against that issuer's own key, audience,
            // lifetime. Everything after this line is reading a document already established as genuine.
            TokenValidationResult result = await jsonWebTokenHandler.ValidateTokenAsync(token, validationParameters);

            if (!result.IsValid)
            {
                // A stable phrase per failure instead of the library's own message, which is free-form
                // prose a package update may reword under whoever reads it in a log or a response.
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

            // Who is calling, which is what every rate limit, dust budget and conversation is attributed
            // to. A token genuine yet anonymous is refused: there would be nobody to attribute them to.
            string? callerId = result.Claims.TryGetValue(JwtRegisteredClaimNames.Sub, out object? subValue) ? subValue?.ToString() : null;
            if (string.IsNullOrEmpty(callerId))
            {
                logger.LogWarning("JWT valid but missing 'sub' claim");
                return new Records.AuthenticationResult(IsAuthenticated: false, Error: "Token is valid but missing required 'sub' claim");
            }

            // "name" is optional on a channel's self-issued token; falling back to the "sub" value
            // (the user id itself) means callers always get a non-null DisplayName to show.
            string? displayName = result.Claims.TryGetValue(JwtRegisteredClaimNames.Name, out object? nameValue) ? nameValue?.ToString() : callerId;

            // The issuer travels back with its role beside it. Both were settled by the check above.
            // A gate admitting only some callers has no other way to know which key opened it.
            return new Records.AuthenticationResult(
                IsAuthenticated: true,
                CallerId: callerId,
                DisplayName: displayName,
                Issuer: issuer,
                IssuerType: issuerTypesByName[issuer]);
        }
        catch (Exception ex)
        {
            // Fail-closed on anything unforeseen: this path must never answer "authenticated" because
            // something it did not anticipate went wrong on the way.
            logger.LogWarning(ex, "JWT rejected: validation failed");
            return new Records.AuthenticationResult(IsAuthenticated: false, Error: "Token validation failed");
        }
    }
}