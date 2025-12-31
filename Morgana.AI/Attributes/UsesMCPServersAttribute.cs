namespace Morgana.AI.Attributes;

/// <summary>
/// Declares which MCP servers an agent wants to integrate with.
/// MCP tools from specified servers will be automatically loaded and registered.
/// </summary>
/// <example>
/// [HandlesIntent("troubleshooting")]
/// [UsesMCPServers("HardwareCatalog", "SecurityCatalog")]
/// public class TroubleshootingAgent : MorganaAgent { }
/// </example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class UsesMCPServersAttribute : Attribute
{
    /// <summary>
    /// Names of MCP servers this agent requires.
    /// Must match server names in appsettings.json LLM:MCPServers configuration.
    /// </summary>
    public string[] ServerNames { get; }

    public UsesMCPServersAttribute(params string[] serverNames)
    {
        ServerNames = serverNames ?? [];
    }
}