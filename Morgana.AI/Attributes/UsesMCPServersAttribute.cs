namespace Morgana.AI.Attributes;

/// <summary>
/// Attribute to declare which MCP servers an agent requires.
/// Applied to agent classes to enable automatic MCP tool discovery and registration.
/// </summary>
/// <remarks>
/// <para><strong>Usage:</strong></para>
/// <code>
/// [HandlesIntent("billing")]
/// [UsesMCPServers("BillingServer", "PaymentGateway")]
/// public class BillingAgent : MorganaAgent
/// {
///     // Agent automatically gets tools from both MCP servers during creation
/// }
/// </code>
/// 
/// <para><strong>Behavior:</strong></para>
/// <para>During agent creation, AgentAdapter will:</para>
/// <list type="number">
/// <item>Detect this attribute via reflection</item>
/// <item>Connect to each declared server via MCPClientService</item>
/// <item>Discover tools from each server via MCPClient</item>
/// <item>Convert and register tools via MCPAdapter</item>
/// <item>Register tools with prefixed names (e.g., BillingServer_query_database)</item>
/// </list>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public class UsesMCPServersAttribute : Attribute
{
    /// <summary>
    /// Names of MCP servers this agent requires.
    /// Must match server names in appsettings.json configuration.
    /// </summary>
    public string[] ServerNames { get; }

    /// <summary>
    /// Initializes a new instance of UsesMCPServersAttribute.
    /// </summary>
    /// <param name="serverNames">Names of MCP servers to connect to</param>
    public UsesMCPServersAttribute(params string[] serverNames)
    {
        ServerNames = serverNames ?? [];
    }
}