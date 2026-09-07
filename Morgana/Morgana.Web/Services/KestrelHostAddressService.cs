using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Morgana.AI.Interfaces;

namespace Morgana.Web.Services;

/// <summary>
/// <see cref="IHostAddressService"/> reading the address Kestrel actually bound, so nothing has to
/// declare the application's own URL. Answers <c>null</c> until the server has bound.
/// </summary>
/// <remarks>
/// <para>A wildcard binding (<c>http://+:8080</c>, <c>0.0.0.0</c>, <c>[::]</c>) names no dialable host
/// and is answered with loopback on the same port — right for what this address serves: an agent
/// calling an agent of the same instance never leaves the machine.</para>
///
/// <para>An instance reached through something that terminates the connection in front of it — an
/// ingress, a reverse proxy, a published container port — is the one case the binding cannot answer
/// for: what it bound is not where a peer reaches it and a card naming the binding is refused by
/// every consumer, which requires an advertised interface to be the origin the card was fetched from.
/// Such a deployment declares that address as <c>Morgana:AgentToAgent:PublicUrl</c> and it is
/// preferred here over anything the server reports.</para>
/// </remarks>
public class KestrelHostAddressService : IHostAddressService
{
    /// <summary>Where a peer reaches this installation, when something in front of it terminates the connection.</summary>
    private const string PublicAddressKey = "Morgana:AgentToAgent:PublicUrl";

    /// <summary>Hosts that name every interface rather than one and so cannot be dialed as written.</summary>
    private static readonly string[] WildcardHosts = ["+", "*", "0.0.0.0", "[::]", "::"];

    /// <summary>The running server, queried for the addresses it bound.</summary>
    private readonly IServer server;

    /// <summary>Read for the address a deployment declares itself reachable at from outside.</summary>
    private readonly IConfiguration configuration;

    /// <summary>Logger for address-resolution diagnostics.</summary>
    private readonly ILogger<KestrelHostAddressService> logger;

    /// <summary>Builds the service over the server whose bindings it reports.</summary>
    /// <param name="server">The running web server.</param>
    /// <param name="configuration">Application configuration, read for a declared public address.</param>
    /// <param name="logger">Logger for address-resolution diagnostics.</param>
    public KestrelHostAddressService(IServer server, IConfiguration configuration, ILogger<KestrelHostAddressService> logger)
    {
        this.server = server;
        this.configuration = configuration;
        this.logger = logger;
    }

    /// <inheritdoc />
    public string? ResolveBaseAddress()
    {
        // Declared, it is the whole answer: it is where peers reach this installation, so it is also
        // where this installation must reach itself. Sending the ring to the binding instead would
        // have an agent read its own colleague's card at one origin while that card names another,
        // which is the very mismatch a consumer refuses a colleague for.
        if (configuration[PublicAddressKey] is { } declaredPublicAddress && !string.IsNullOrWhiteSpace(declaredPublicAddress))
            return declaredPublicAddress.Trim().TrimEnd('/');

        ICollection<string>? boundAddresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;

        if (boundAddresses is null || boundAddresses.Count == 0)
        {
            logger.LogWarning("The server reports no bound address yet: agents of this instance cannot publish a callable A2A interface");
            return null;
        }

        // https first when both are bound: a card advertising the plaintext endpoint of an instance
        // that also serves TLS would have callers downgrade for no reason.
        string boundAddress = boundAddresses.FirstOrDefault(address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                              ?? boundAddresses.First();

        return ToDialableAddress(boundAddress);
    }

    /// <summary>
    /// Turns a bound address into one that can actually be called, replacing a wildcard host with
    /// the loopback interface and dropping any trailing separator.
    /// </summary>
    /// <param name="boundAddress">Address as the server reports it.</param>
    private string ToDialableAddress(string boundAddress)
    {
        string trimmedAddress = boundAddress.TrimEnd('/');

        if (!Uri.TryCreate(trimmedAddress, UriKind.Absolute, out Uri? parsedAddress)
             || !WildcardHosts.Contains(parsedAddress.Host, StringComparer.OrdinalIgnoreCase))
            return trimmedAddress;

        string loopbackAddress = $"{parsedAddress.Scheme}://127.0.0.1:{parsedAddress.Port}";

        logger.LogInformation("Server bound to the wildcard address {BoundAddress}; agents will reach each other over {LoopbackAddress}", trimmedAddress, loopbackAddress);

        return loopbackAddress;
    }
}