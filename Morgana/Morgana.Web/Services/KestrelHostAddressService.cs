using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Morgana.AI.Interfaces;

namespace Morgana.Web.Services;

/// <summary>
/// <see cref="IHostAddressService"/> reading the address Kestrel actually bound, so nothing has to
/// declare the application's own URL. Answers <c>null</c> until the server has bound.
/// </summary>
/// <remarks>
/// A wildcard binding (<c>http://+:8080</c>, <c>0.0.0.0</c>, <c>[::]</c>) names no dialable host and
/// is answered with loopback on the same port — right for what this address serves: an agent calling
/// an agent of the same instance never leaves the machine.
/// </remarks>
public class KestrelHostAddressService : IHostAddressService
{
    /// <summary>Hosts that name every interface rather than one, and so cannot be dialed as written.</summary>
    private static readonly string[] WildcardHosts = ["+", "*", "0.0.0.0", "[::]", "::"];

    /// <summary>The running server, queried for the addresses it bound.</summary>
    private readonly IServer server;

    /// <summary>Logger for address-resolution diagnostics.</summary>
    private readonly ILogger<KestrelHostAddressService> logger;

    /// <summary>Builds the service over the server whose bindings it reports.</summary>
    /// <param name="server">The running web server.</param>
    /// <param name="logger">Logger for address-resolution diagnostics.</param>
    public KestrelHostAddressService(IServer server, ILogger<KestrelHostAddressService> logger)
    {
        this.server = server;
        this.logger = logger;
    }

    /// <inheritdoc />
    public string? ResolveBaseAddress()
    {
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