namespace Morgana.AI.Attributes;

/// <summary>Declares one MCP server whose tools the agent receives, repeatable for several servers.</summary>
/// <remarks>Write <c>[UsesMCPServer("https://...")]</c> for a remote server and <c>[UsesMCPServer(MCPTransport.Stdio, "path/to/cmd", args)]</c> for a local process.</remarks>
// The agent receives the tools that the tool guard admitted from the server; with that guard switched off
// it receives every tool the server lists. Declaring a server opens a door Morgana cannot see behind: the guard reads what the server offers
// and what it returns, never what it does with what it is sent. The declaration trusts the server with every
// argument that an agent passes it, the user's data included.
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public class UsesMCPServerAttribute : Attribute
{
    /// <summary>How Morgana reaches the server.</summary>
    public Records.MCPTransport Transport { get; }

    /// <summary>The absolute URI of a remote server or the path of the executable of a local one.</summary>
    public string Command { get; }

    /// <summary>The arguments of a local server's process; empty for a remote server.</summary>
    public string[] Args { get; }

    /// <summary>Declares a remote server reached over HTTP or HTTPS.</summary>
    /// <param name="uri">The absolute http or https URI of the server.</param>
    /// <exception cref="ArgumentException">The URI is not an absolute http or https URI.</exception>
    public UsesMCPServerAttribute(string uri)
        : this(Records.MCPTransport.Http, uri) { }

    /// <summary>Declares a server reached over either transport.</summary>
    /// <param name="transport">How Morgana reaches the server.</param>
    /// <param name="command">The absolute http or https URI for <see cref="Records.MCPTransport.Http"/>; the path of the executable to spawn for <see cref="Records.MCPTransport.Stdio"/>.</param>
    /// <param name="args">The arguments of the process, read only for <see cref="Records.MCPTransport.Stdio"/>.</param>
    /// <exception cref="ArgumentException">An <see cref="Records.MCPTransport.Http"/> server is not named by an absolute http or https URI.</exception>
    public UsesMCPServerAttribute(Records.MCPTransport transport, string command, params string[] args)
    {
        // A remote server is reached at its URI, so one that cannot be called fails where it is declared instead of at the first agent built.
        if (transport == Records.MCPTransport.Http)
        {
            // Only an absolute address on a web scheme names a server that the MCP client can call.
            if (!Uri.TryCreate(command, UriKind.Absolute, out Uri? serverUri) ||
                (serverUri.Scheme != "https" && serverUri.Scheme != "http"))
            {
                throw new ArgumentException(
                    $"'{command}' is not a valid absolute http/https URI. " +
                    $"UsesMCPServer with Http transport requires an absolute URI " +
                    $"(e.g. \"https://my-mcp.azurewebsites.net/\").",
                    nameof(command));
            }
        }

        // The MCP registry keys its connection pool on the transport and the command.
        Transport = transport;
        Command = command;

        // A declaration that passes null explicitly is read as no arguments, so no reader of Args meets a null.
        Args = args ?? [];
    }
}