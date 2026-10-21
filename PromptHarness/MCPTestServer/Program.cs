using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// The argument after --http names the address to serve on; without it the server speaks over stdio.
int httpFlagIndex = Array.IndexOf(args, "--http");

// The HTTP server is the one the harness starts on its fixed local port, as a remote server would be reached.
if (httpFlagIndex >= 0)
{
    // Nothing of the command line reaches the host's configuration: the address is the only setting.
    WebApplicationBuilder webApplicationBuilder = WebApplication.CreateBuilder();

    // The harness keeps the server's error stream for its failure message, so no logger writes to it.
    webApplicationBuilder.Logging.ClearProviders();

    // The server listens only where the agent under test was declared to reach it.
    webApplicationBuilder.WebHost.UseUrls(args[httpFlagIndex + 1]);

    // The same tools as over stdio, served over Streamable HTTP.
    RegisterTools(webApplicationBuilder.Services.AddMcpServer().WithHttpTransport());

    // The protocol is answered at the root of the address, where the declaration points.
    WebApplication webApplication = webApplicationBuilder.Build();
    webApplication.MapMcp();

    // Serves until the harness kills the process.
    await webApplication.RunAsync();
}
// The stdio server is the one an agent starts itself, speaking over the process's pipes.
else
{
    HostApplicationBuilder builder = Host.CreateApplicationBuilder();

    // Stdout carries the protocol, so no logger may write to it.
    builder.Logging.ClearProviders();

    // The same tools as over HTTP, served over the process's pipes.
    RegisterTools(builder.Services.AddMcpServer().WithStdioServerTransport());

    // Serves until the client closes the pipes.
    await builder.Build().RunAsync();
}

// Registers the tools that both transports serve, in the order the server declares them.
void RegisterTools(IMcpServerBuilder server)
{
    // The tools every test server offers.
    server.WithTools<WorkflowTools>();

    // The clashing tool exists only on request: it shares its name with a native tool of the test rig.
    if (args.Contains("--clash", StringComparer.Ordinal))
        server.WithTools<ClashingTools>();
}

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
