using System.Globalization;
using Examples.Agents;
using Examples.Data;
using GardeningMCP;

// The server speaks the invariant culture whatever the host is set to, so the month names and the
// figures that the almanac hands to a model read the same on a workstation set to it-IT and in a
// container with no LANG at all.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

// The web application that hosts the almanac server.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// The plugin's database helper reads the location from the environment, so a value that came from
// configuration or user secrets has to be forwarded there.
string? conversationStoragePath = builder.Configuration["Morgana:ConversationPersistence:StoragePath"];
if (!string.IsNullOrWhiteSpace(conversationStoragePath))
    Environment.SetEnvironmentVariable("Morgana__ConversationPersistence__StoragePath", conversationStoragePath);

// The database with its almanac is deployed before the first question, whichever of Morgana and this server starts first.
ExamplesDatabaseHelper.Ensure();

// The three tools of the head gardener are what the server offers to the agents that connect to it.
builder.Services.AddMcpServer().WithHttpTransport().WithTools<GardeningTools>();

// The server binds where the shipped agent looks for it, whatever the environment says, so the agent
// and the server never disagree about the address.
Uri almanacServerAddress = new Uri(GardeningAgent.AlmanacServerAddress);
builder.WebHost.UseUrls(almanacServerAddress.GetLeftPart(UriPartial.Authority));

// The services are fixed from here on: what remains is where the almanac answers.
WebApplication application = builder.Build();

// The MCP endpoint sits at the path of the address that the shipped agent names.
application.MapMcp(almanacServerAddress.AbsolutePath);

// The almanac answers for as long as the process lives: in Docker, the life of the container.
await application.RunAsync();