using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Proves a caller's JWT, signed with HMAC-SHA256, against the key of the issuer it names and against no other.
/// A channel comes from <c>Morgana:Authentication:Issuers</c>, a partner from <c>Morgana:AgentToAgent:Partners</c> and this installation's
/// own agents sign with a secret coined at start: what a caller is follows from where its key was found and cannot be asserted in a declaration.
/// </summary>
public class JWTAuthenticationService : IAuthenticationService
{
    /// <summary>
    /// The validation of each declared issuer, pinned to that issuer's own key; an issuer absent from it is rejected outright.
    /// The pinning keeps a leaked channel key from validating another channel's tokens.
    /// </summary>
    private readonly Dictionary<string, TokenValidationParameters> validationParametersByIssuer;

    /// <summary>
    /// The issuers whose key came from a partner declaration or from this installation's own ring, as a successful result reports.
    /// The conversation API serves channels and the A2A door serves partners, so each gate has to know which of the two opened it.
    /// </summary>
    private readonly HashSet<string> partnerIssuers;

    /// <summary>Reads and proves the tokens; it holds no state, so every call shares it.</summary>
    private readonly JsonWebTokenHandler jsonWebTokenHandler = new JsonWebTokenHandler();

    /// <summary>
    /// Logs the issuers admitted at startup and the reason of each rejection.
    /// The caller only ever sees a refusal, so the reason lives here.
    /// </summary>
    private readonly ILogger logger;

    /// <summary>
    /// Admits the declared issuers, refusing at startup any declaration that a caller could never be proven against.
    /// What is accepted decides which door a key opens, so a defect found later would be a caller wrongly admitted.
    /// </summary>
    /// <param name="options">The <c>Morgana:Authentication</c> section.</param>
    /// <param name="configuration">Application configuration, read for the partners this installation federates with.</param>
    /// <param name="peerRingKeyService">Holds the secret this installation's own consultations are signed with.</param>
    /// <param name="logger">Logger for the issuers configured here and for every later rejection.</param>
    /// <exception cref="InvalidOperationException">A declaration is missing, weak or duplicated.</exception>
    public JWTAuthenticationService(
        IOptions<Records.AuthenticationOptions> options,
        IConfiguration configuration,
        PeerRingKeyService peerRingKeyService,
        ILogger logger)
    {
        this.logger = logger;

        // The audience and the channels of Morgana:Authentication.
        Records.AuthenticationOptions authenticationOptions = options.Value;

        // With no channel at all this installation answers no users. It may still serve partners, so
        // that alone is not fatal — but a deployment reaching nobody is almost always a missing entry.
        if (authenticationOptions.Issuers is null || authenticationOptions.Issuers.Count == 0)
        {
            logger.LogWarning(
                "No channel is declared under 'Morgana:Authentication:Issuers': this installation serves no users, only whatever partners it federates with.");
        }

        // An issuer name is matched exactly as the token writes it.
        validationParametersByIssuer = new Dictionary<string, TokenValidationParameters>(StringComparer.Ordinal);
        partnerIssuers = new HashSet<string>(StringComparer.Ordinal);

        // Every channel, each pinned to its own key.
        foreach (Records.IssuerOptions configuredChannel in authenticationOptions.Issuers ?? [])
            AdmitIssuer(configuredChannel.Name, configuredChannel.SymmetricKey, authenticationOptions.Audience, isPartner: false);

        // A partner brings its key on the entry that also says how far it reaches, so being admitted
        // here and being admitted at an agent are read from one declaration. Only the direction that
        // actually arrives is registered: a partner this installation only calls proves nothing here.
        foreach ((string issuer, Records.PartnerOptions partner) in ConfigurationAgentDirectoryService.ResolveAdmittedPartners(configuration))
            AdmitIssuer(issuer, partner.SymmetricKey, authenticationOptions.Audience, isPartner: true);

        // This installation's own agents, under a secret nobody configured. Registered unconditionally
        // because a consultation between them is signed with it whether or not any partner exists.
        AdmitIssuer(Constants.AgentToAgent.IssuerName, peerRingKeyService.SymmetricKey, authenticationOptions.Audience, isPartner: true);

        // The whole trust surface of this installation on one line, which is what an operator reads back
        // to see that a channel was onboarded as a channel rather than as a peer.
        this.logger.LogInformation(
            "JWT authentication initialized — audience: {Audience}, issuers: [{Issuers}]",
            authenticationOptions.Audience,
            string.Join(", ", validationParametersByIssuer.Keys.Select(name => $"{name} ({(partnerIssuers.Contains(name) ? "partner" : "channel")})")));
    }

    /// <summary>Admits one issuer, pinning it to its own key and recording which door that key opens.</summary>
    /// <param name="issuerName">Name expected in the <c>iss</c> claim.</param>
    /// <param name="symmetricKey">Key tokens naming it are proven against.</param>
    /// <param name="audience">Audience every caller of this installation must address.</param>
    /// <param name="isPartner">Whether the key came from a partner declaration rather than a channel's.</param>
    /// <exception cref="InvalidOperationException">The declaration is nameless, keyless, weak or duplicated.</exception>
    private void AdmitIssuer(string issuerName, string symmetricKey, string audience, bool isPartner)
    {
        // Nothing is admitted before it is proven usable. The names accepted so far travel with the
        // entry, so a duplicate is weighed against what actually got in rather than against what
        // somebody wrote down — a channel and a partner sharing a name included.
        byte[] signingKey = ValidateIssuerDeclaration(issuerName, symmetricKey, validationParametersByIssuer.Keys);

        // The entry that admits this caller at all: from here a token naming this issuer is proven
        // against this key alone, never against another's.
        validationParametersByIssuer[issuerName] =
            new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(signingKey),
                ValidateIssuer = true,
                ValidIssuer = issuerName,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };

        // A partner's key opens the A2A door, a channel's the conversation API.
        if (isPartner)
            partnerIssuers.Add(issuerName);
    }

    /// <summary>Refuses an issuer declaration that could never prove a caller and hands back its signing key.</summary>
    /// <param name="issuerName">Name the declaration expects in the <c>iss</c> claim.</param>
    /// <param name="symmetricKey">Key it signs with.</param>
    /// <param name="alreadyDeclared">Names accepted so far, against which this one must be new.</param>
    /// <returns>The signing key, proven to be at least 256 bits.</returns>
    /// <exception cref="InvalidOperationException">The declaration is incomplete, weak or duplicated.</exception>
    private static byte[] ValidateIssuerDeclaration(string issuerName, string symmetricKey, ICollection<string> alreadyDeclared)
    {
        // Nameless, so there is nothing for an iss claim to match.
        if (string.IsNullOrWhiteSpace(issuerName))
        {
            throw new InvalidOperationException(
                        "Morgana authentication issuer entry is missing 'Name'.");
        }

        // Keyless, so nothing this issuer signs could ever be proven: it would be refused at every door.
        if (string.IsNullOrWhiteSpace(symmetricKey))
        {
            throw new InvalidOperationException(
                        $"Morgana authentication issuer '{issuerName}' has no SymmetricKey configured.");
        }

        // HMAC-SHA256 needs a key at least as long as its output to deliver its full security margin.
        // Startup-fatal, not a warning: a weak signing key is exploitable rather than degraded.
        byte[] keyBytes = Encoding.UTF8.GetBytes(symmetricKey);
        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                        $"Morgana authentication SymmetricKey for issuer '{issuerName}' must be at least 256 bits (32 bytes). " +
                        $"Current key is {keyBytes.Length * 8} bits.");
        }

        // One name declared twice leaves one of the two keys silently unusable. Which one survives
        // depends on the order somebody happened to write them in. A channel colliding with a
        // partner would decide by that order which of the two doors the surviving key opens.
        if (alreadyDeclared.Contains(issuerName))
        {
            throw new InvalidOperationException(
                        $"Morgana authentication issuer '{issuerName}' is declared more than once, as a channel under "
                        + "Morgana:Authentication:Issuers or as a partner under Morgana:AgentToAgent:Partners.");
        }

        // The key, proven wide enough, which the issuer's validation is pinned to.
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
            TokenValidationResult validationResult = await jsonWebTokenHandler.ValidateTokenAsync(token, validationParameters);

            // A token that fails a check is refused with the check it failed.
            if (!validationResult.IsValid)
            {
                // A stable phrase per failure instead of the library's own message, which is free-form
                // prose a package update may reword under whoever reads it in a log or a response.
                string error = validationResult.Exception switch
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
            string? callerId = validationResult.Claims.TryGetValue(JwtRegisteredClaimNames.Sub, out object? subValue) ? subValue?.ToString() : null;
            if (string.IsNullOrEmpty(callerId))
            {
                logger.LogWarning("JWT valid but missing 'sub' claim");
                return new Records.AuthenticationResult(IsAuthenticated: false, Error: "Token is valid but missing required 'sub' claim");
            }

            // "name" is optional on a channel's self-issued token; falling back to the "sub" value
            // (the user id itself) means callers always get a non-null DisplayName to show.
            string? displayName = validationResult.Claims.TryGetValue(JwtRegisteredClaimNames.Name, out object? nameValue) ? nameValue?.ToString() : callerId;

            // The issuer travels back with the door its key was cut for beside it. A gate admitting
            // only some callers has no other way to know which of the two registries proved this one.
            return new Records.AuthenticationResult(
                IsAuthenticated: true,
                CallerId: callerId,
                DisplayName: displayName,
                Issuer: issuer,
                IsPartner: partnerIssuers.Contains(issuer));
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