using System.Net;

namespace Morgana.AI.Attributes;

/// <summary>
/// Declares a single MCP server dependency for an agent.
/// Apply multiple times to declare multiple servers.
/// </summary>
/// <remarks>
/// <para><strong>Usage — HTTP/HTTPS server:</strong></para>
/// <code>
/// [HandlesIntent("monkeys")]
/// [UsesMCPServer("https://func-monkeymcp-3t4eixuap5dfm.azurewebsites.net/")]
/// public class MonkeyAgent : MorganaAgent { }
/// </code>
///
/// <para><strong>Usage — stdio server:</strong></para>
/// <code>
/// [HandlesIntent("filesystem")]
/// [UsesMCPServer(MCPTransport.Stdio, "/usr/local/bin/mcp-filesystem", "--root", "/data")]
/// public class FilesystemAgent : MorganaAgent { }
/// </code>
///
/// <para><strong>Usage — mixed:</strong></para>
/// <code>
/// [HandlesIntent("filesystem")]
/// [UsesMCPServer("https://my-remote-mcp.azurewebsites.net/")]
/// [UsesMCPServer(MCPTransport.Stdio, "/usr/local/bin/mcp-filesystem", "--root", "/data")]
/// public class FilesystemAgent : MorganaAgent { }
/// </code>
///
/// <para><strong>Behavior:</strong></para>
/// <para>During agent creation, MorganaAgentAdapter will:</para>
/// <list type="number">
/// <item>Collect all [UsesMCPServer] attributes via reflection</item>
/// <item>Connect to each declared server via IMCPClientRegistryService</item>
/// <item>Discover tools from each server via MCPClient</item>
/// <item>Convert and register tools via MCPToolAdapter</item>
/// <item>Make tools available to the agent during LLM interactions</item>
/// </list>
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

        Transport = transport;
        Command   = command;
        Args      = args ?? [];
    }
}