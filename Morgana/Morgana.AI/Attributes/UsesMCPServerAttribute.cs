using System.Net;

namespace Morgana.AI.Attributes;

/// <summary>
/// Declares a single MCP server dependency for an agent.
/// Apply multiple times to declare multiple servers.
/// </summary>
/// <remarks>
/// Tools acquired from a server never ask the user for approval: they carry none of the framework's
/// attributes and the server's annotations are ignored, readOnlyHint and destructiveHint included.
/// The human in the loop that the MCP specification asks for is the curator who adds the server to the agent.
/// Where one of its tools changes something real, the agent's Instructions have it ask the user first
/// and Reply offers the action button that leads to the tool.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class UsesMCPServerAttribute : Attribute
{
    /// <summary>
    /// Transport mechanism for this MCP server.
    /// </summary>
    public Records.MCPTransport Transport { get; }

    /// <summary>
    /// For Http transport: the absolute URI of the remote MCP server.
    /// For Stdio transport: the path to the local executable.
    /// </summary>
    public string Command { get; }

    /// <summary>
    /// Arguments passed to the process. Only applicable for Stdio transport.
    /// </summary>
    public string[] Args { get; }

    /// <summary>
    /// HTTP/HTTPS server — alias for the primary constructor with <see cref="WebRequestMethods.Http"/>.
    /// </summary>
    /// <param name="uri">Absolute http/https URI of the remote MCP server</param>
    /// <exception cref="ArgumentException">Thrown if the URI is not a valid absolute http/https URI</exception>
    public UsesMCPServerAttribute(string uri)
        : this(Records.MCPTransport.Http, uri) { }

    /// <summary>
    /// Primary constructor. Covers both Http and Stdio transports.
    /// </summary>
    /// <param name="transport">Transport mechanism to use</param>
    /// <param name="command">
    /// For <see cref="WebRequestMethods.Http"/>: absolute http/https URI of the remote server.<br/>
    /// For <see cref="Records.MCPTransport.Stdio"/>: path to the local executable to spawn.
    /// </param>
    /// <param name="args">
    /// Arguments passed to the process. Only used for <see cref="Records.MCPTransport.Stdio"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown if Http transport is used with an invalid absolute http/https URI.
    /// </exception>
    public UsesMCPServerAttribute(Records.MCPTransport transport, string command, params string[] args)
    {
        // An HTTP server is reached by address, so a malformed one is refused when the class is read rather than at the first call.
        if (transport == Records.MCPTransport.Http)
        {
            if (!Uri.TryCreate(command, UriKind.Absolute, out Uri? parsed) ||
                (parsed.Scheme != "https" && parsed.Scheme != "http"))
            {
                throw new ArgumentException(
                    $"'{command}' is not a valid absolute http/https URI. " +
                    $"UsesMCPServer with Http transport requires an absolute URI " +
                    $"(e.g. \"https://my-mcp.azurewebsites.net/\").",
                    nameof(command));
            }
        }

        // Stdio servers take their launch arguments here; an HTTP server has none.
        Transport = transport;
        Command   = command;
        Args      = args ?? [];
    }
}