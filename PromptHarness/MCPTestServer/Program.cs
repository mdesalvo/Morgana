using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

HostApplicationBuilder builder = Host.CreateApplicationBuilder();

// Stdout carries the protocol, so no logger may write to it.
builder.Logging.ClearProviders();

IMcpServerBuilder server = builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<WorkflowTools>();

// The clashing tool exists only on request: it shares its name with a native tool of the test rig.
if (args.Contains("--clash", StringComparer.Ordinal))
    server.WithTools<ClashingTools>();

await builder.Build().RunAsync();

/// <summary>What the stock reservation answers: a failure marker, the order and the quantity.</summary>
public sealed record Reservation(string? Error, string? OrderId, int Quantity);

/// <summary>What the shipment answers: a failure marker and the tracking code.</summary>
public sealed record Shipment(string? Error, string? TrackingCode);

/// <summary>The tools that every test server offers.</summary>
[McpServerToolType]
public sealed class WorkflowTools
{
    /// <summary>Reserves one unit of an item, failing for the item named "unavailable".</summary>
    [McpServerTool(Name = "ReserveStock", UseStructuredContent = true), Description("Reserves one unit of an item")]
    public static Reservation ReserveStock([Description("The item to reserve")] string item)
        => item == "unavailable"
            ? new Reservation("out of stock", null, 0)
            : new Reservation(null, "RSV-" + item, 1);

    /// <summary>Ships a reserved order.</summary>
    [McpServerTool(Name = "ShipOrder", UseStructuredContent = true), Description("Ships an order")]
    public static Shipment ShipOrder([Description("The order to ship")] string orderId)
        => new Shipment(null, "TRK-" + orderId);

    /// <summary>Answers a plain text, so the server declares no output schema.</summary>
    [McpServerTool(Name = "ReadNote"), Description("Reads the note")]
    public static string ReadNote() => "noted";

    /// <summary>Always throws, which the protocol carries as an error result.</summary>
    [McpServerTool(Name = "FailHard"), Description("Fails every time")]
    public static string FailHard() => throw new InvalidOperationException("failed on purpose");

    /// <summary>Declared destructive, which the framework ignores.</summary>
    [McpServerTool(Name = "WipeCatalog", Destructive = true), Description("Wipes the catalog")]
    public static string WipeCatalog() => "wiped";
}

/// <summary>The tool that collides with a native one.</summary>
[McpServerToolType]
public sealed class ClashingTools
{
    /// <summary>Answers a plain text under the name of a native tool.</summary>
    [McpServerTool(Name = "Stock"), Description("Reads the stock of an item")]
    public static string Stock([Description("The item to read")] string item) => "mcp-stock-" + item;
}
