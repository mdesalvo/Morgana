using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Morgana.AI.Interfaces;

namespace Morgana.Web.Services;

/// <summary>
/// Reports where this installation is reached: the declared <c>Morgana:AgentToAgent:PublicUrl</c>, otherwise the address Kestrel bound; null before it binds.
/// A wildcard binding becomes loopback on the same port, since an agent calling an agent of the same instance never leaves the machine,
/// while a deployment behind an ingress or a published container port declares its public address, which the binding cannot know.
/// </summary>
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

    /// <summary>Records a wildcard binding answered with loopback.</summary>
    private readonly ILogger<KestrelHostAddressService> logger;

    /// <summary>Builds the service over the server whose bindings it reports.</summary>
    /// <param name="server">The running web server.</param>
    /// <param name="configuration">Application configuration, read for a declared public address.</param>
    /// <param name="logger">Records a wildcard binding answered with loopback.</param>
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

        // Nothing is bound before Kestrel starts listening: the question is asked again once it has.
        ICollection<string>? boundAddresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;

        // Without a bound address there is no interface to publish yet. That is the ordinary state while the
        // cards are projected before Kestrel listens, so the caller alone judges whether the absence is a fault.
        if (boundAddresses is null || boundAddresses.Count == 0)
            return null;

        // https first when both are bound: a card advertising the plaintext endpoint of an instance
        // that also serves TLS would have callers downgrade for no reason.
        string boundAddress = boundAddresses.FirstOrDefault(address => address.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                              ?? boundAddresses.First();

        // What the server reported, made callable: a wildcard host is no address a caller can dial.
        return ToDialableAddress(boundAddress);
    }

    /// <summary>Turns a bound address into one that can be called, replacing a wildcard host with loopback and dropping any trailing separator.</summary>
    /// <param name="boundAddress">Address as the server reports it.</param>
    private string ToDialableAddress(string boundAddress)
    {
        // The agent path is appended after a single separator, so the base carries none of its own.
        string trimmedAddress = boundAddress.TrimEnd('/');

        // A named host is already what a caller dials, as is anything that does not parse as a URI.
        if (!Uri.TryCreate(trimmedAddress, UriKind.Absolute, out Uri? parsedAddress)
             || !WildcardHosts.Contains(parsedAddress.Host, StringComparer.OrdinalIgnoreCase))
            return trimmedAddress;

        // The same scheme and port on loopback, where the wildcard binding also listens.
        string loopbackAddress = $"{parsedAddress.Scheme}://127.0.0.1:{parsedAddress.Port}";

        logger.LogInformation("Server bound to the wildcard address {BoundAddress}; agents will reach each other over {LoopbackAddress}", trimmedAddress, loopbackAddress);

        // The address the cards advertise and the ring calls.
        return loopbackAddress;
    }
}