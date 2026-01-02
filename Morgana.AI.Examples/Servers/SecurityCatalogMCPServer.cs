using System.Text.Json;
using Microsoft.Extensions.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Types;
using Morgana.AI.Servers;
using WireMock;
using WireMock.Util;

namespace Morgana.AI.Examples.Servers;

/// <summary>
/// Security software catalog MCP server with embedded WireMock HTTP server.
/// This server demonstrates how to create a mock HTTP MCP server that:
/// 1. Extends MorganaHttpMCPServer (consumes MCP tools via HTTP)
/// 2. Starts its own WireMock server in the constructor
/// 3. Responds to MCP protocol endpoints with mock data
/// 
/// IMPORTANT: This is a MOCK/EXAMPLE implementation for demonstration purposes.
/// In production, you would connect to real security product databases.
/// </summary>
public class SecurityCatalogMCPServer : MorganaHttpMCPServer
{
    private readonly WireMockServer wireMockServer;
    private readonly SecurityCatalogData mockData;
    
    public SecurityCatalogMCPServer(
        Records.MCPServerConfig config,
        ILogger logger,
        IHttpClientFactory httpClientFactory) : base(config, logger, httpClientFactory)
    {
        // Initialize mock data
        mockData = new SecurityCatalogData();
        
        // Extract port from Endpoint
        if (config.AdditionalSettings?.TryGetValue("Endpoint", out string? endpoint) != true || string.IsNullOrEmpty(endpoint))
            throw new InvalidOperationException($"SecurityCatalog requires 'Endpoint' in AdditionalSettings");
        
        int port = new Uri(endpoint!).Port;
        
        // Start WireMock server
        wireMockServer = WireMockServer.Start(port);
        
        // Configure MCP protocol endpoints
        ConfigureMCPEndpoints();
        
        logger.LogInformation($"🎭 SecurityCatalog WireMock server started on {endpoint}");
    }
    
    private void ConfigureMCPEndpoints()
    {
        // GET /mcp/tools - List all available tools
        wireMockServer
            .Given(Request.Create()
                .WithPath("/mcp/tools")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBodyAsJson(mockData.GetToolDefinitions()));
        
        // POST /mcp/tools/CercaSoftwareSicurezza
        wireMockServer
            .Given(Request.Create()
                .WithPath("/mcp/tools/CercaSoftwareSicurezza")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithCallback(HandleCercaSoftwareSicurezza));
        
        // POST /mcp/tools/OttieniDettagliSoftwareSicurezza
        wireMockServer
            .Given(Request.Create()
                .WithPath("/mcp/tools/OttieniDettagliSoftwareSicurezza")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithCallback(HandleOttieniDettagliSoftwareSicurezza));
        
        // POST /mcp/tools/VerificaCompatibilitaMinaccia
        wireMockServer
            .Given(Request.Create()
                .WithPath("/mcp/tools/VerificaCompatibilitaMinaccia")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithCallback(HandleVerificaCompatibilitaMinaccia));
    }
    
    private ResponseMessage HandleCercaSoftwareSicurezza(IRequestMessage request)
    {
        try
        {
            Records.InvokeToolRequest? body = JsonSerializer.Deserialize<Records.InvokeToolRequest>(request.Body);
            Records.MCPToolResult result = mockData.SearchSecuritySoftware(body?.Parameters ?? new Dictionary<string, object>());
            
            return new ResponseMessage
            {
                StatusCode = 200,
                Headers = new Dictionary<string, WireMockList<string>> { ["Content-Type"] = "application/json" },
                BodyData = new BodyData
                {
                    DetectedBodyType = BodyType.Json,
                    BodyAsJson = result
                }
            };
        }
        catch (Exception ex)
        {
            return new ResponseMessage
            {
                StatusCode = 500,
                Headers = new Dictionary<string, WireMockList<string>> { ["Content-Type"] = "application/json" },
                BodyData = new BodyData
                {
                    DetectedBodyType = BodyType.Json,
                    BodyAsJson = new Records.MCPToolResult(
                        IsError: true,
                        Content: null,
                        ErrorMessage: $"Error: {ex.Message}")
                }
            };
        }
    }
    
    private ResponseMessage HandleOttieniDettagliSoftwareSicurezza(IRequestMessage request)
    {
        try
        {
            Records.InvokeToolRequest? body = JsonSerializer.Deserialize<Records.InvokeToolRequest>(request.Body);
            Records.MCPToolResult result = mockData.GetSecurityDetails(body?.Parameters ?? new Dictionary<string, object>());
            
            return new ResponseMessage
            {
                StatusCode = 200,
                Headers = new Dictionary<string, WireMockList<string>> { ["Content-Type"] = "application/json" },
                BodyData = new BodyData
                {
                    DetectedBodyType = BodyType.Json,
                    BodyAsJson = result
                }
            };
        }
        catch (Exception ex)
        {
            return new ResponseMessage
            {
                StatusCode = 500,
                Headers = new Dictionary<string, WireMockList<string>> { ["Content-Type"] = "application/json" },
                BodyData = new BodyData
                {
                    DetectedBodyType = BodyType.Json,
                    BodyAsJson = new Records.MCPToolResult(
                        IsError: true,
                        Content: null,
                        ErrorMessage: $"Error: {ex.Message}")
                }
            };
        }
    }
    
    private ResponseMessage HandleVerificaCompatibilitaMinaccia(IRequestMessage request)
    {
        try
        {
            Records.InvokeToolRequest? body = JsonSerializer.Deserialize<Records.InvokeToolRequest>(request.Body);
            Records.MCPToolResult result = mockData.CheckThreatCompatibility(body?.Parameters ?? new Dictionary<string, object>());
            
            return new ResponseMessage
            {
                StatusCode = 200,
                Headers = new Dictionary<string, WireMockList<string>> { ["Content-Type"] = "application/json" },
                BodyData = new BodyData
                {
                    DetectedBodyType = BodyType.Json,
                    BodyAsJson = result
                }
            };
        }
        catch (Exception ex)
        {
            return new ResponseMessage
            {
                StatusCode = 500,
                Headers = new Dictionary<string, WireMockList<string>> { ["Content-Type"] = "application/json" },
                BodyData = new BodyData
                {
                    DetectedBodyType = BodyType.Json,
                    BodyAsJson = new Records.MCPToolResult(
                        IsError: true,
                        Content: null,
                        ErrorMessage: $"Error: {ex.Message}")
                }
            };
        }
    }
    
    // Cleanup WireMock on disposal
    ~SecurityCatalogMCPServer()
    {
        wireMockServer?.Stop();
        wireMockServer?.Dispose();
    }
}

/// <summary>
/// Internal data provider for SecurityCatalog mock responses.
/// Contains in-memory catalog of security products and threat signatures.
/// </summary>
internal class SecurityCatalogData
{
    private readonly List<SecurityProduct> catalog;
    
    public SecurityCatalogData()
    {
        // Initialize in-memory security product catalog
        catalog =
        [
            new SecurityProduct(
                Name: "TotalDefender Pro",
                Type: "antivirus",
                Price: 49.99m,
                ProtectsAgainst: ["virus", "malware", "ransomware", "spyware"],
                Features: ["Scansione real-time", "Protezione web", "Firewall integrato"],
                Available: true),

            new SecurityProduct(
                Name: "SecureShield Plus",
                Type: "antivirus",
                Price: 39.99m,
                ProtectsAgainst: ["virus", "malware", "trojan"],
                Features: ["Scansione programmata", "Quarantena automatica"],
                Available: true),

            new SecurityProduct(
                Name: "CyberGuard Premium",
                Type: "antivirus",
                Price: 69.99m,
                ProtectsAgainst: ["virus", "malware", "ransomware", "spyware", "trojan", "rootkit"],
                Features: ["AI-powered detection", "VPN inclusa", "Password manager"],
                Available: true),

            new SecurityProduct(
                Name: "FireWall Pro Elite",
                Type: "firewall",
                Price: 59.99m,
                ProtectsAgainst: ["intrusion", "ddos", "port-scan"],
                Features: ["Deep packet inspection", "IPS integrato", "Geo-blocking"],
                Available: true),

            new SecurityProduct(
                Name: "SafeVPN Unlimited",
                Type: "vpn",
                Price: 29.99m,
                ProtectsAgainst: ["tracking", "snooping", "mitm"],
                Features: ["256-bit encryption", "Kill switch", "No-logs policy"],
                Available: true)
        ];
    }
    
    public IEnumerable<Records.MCPToolDefinition> GetToolDefinitions()
    {
        return
        [
            new Records.MCPToolDefinition(
                Name: "CercaSoftwareSicurezza",
                Description: "Cerca software di sicurezza in base a tipo, minacce o sintomi. Restituisce lista di prodotti compatibili.",
                InputSchema: new Records.MCPInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, Records.MCPParameterSchema>
                    {
                        ["tipoSoftware"] = new(
                            Type: "string",
                            Description: "Tipo di software di sicurezza da cercare",
                            Enum: [ "antivirus", "firewall", "antimalware", "vpn" ]),
                        ["tipoMinaccia"] = new(
                            Type: "string",
                            Description: "Tipo di minaccia da cui proteggersi (filtro opzionale)",
                            Enum: [ "virus", "malware", "ransomware", "spyware", "trojan" ],
                            Default: null),
                        ["sintomi"] = new(
                            Type: "string",
                            Description: "Sintomi riscontrati dall'utente (filtro opzionale)",
                            Default: null)
                    },
                    Required: ["tipoSoftware"])),
            
            new Records.MCPToolDefinition(
                Name: "OttieniDettagliSoftwareSicurezza",
                Description: "Ottieni dettagli completi di un prodotto di sicurezza specifico incluse features e pricing.",
                InputSchema: new Records.MCPInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, Records.MCPParameterSchema>
                    {
                        ["nomeProdotto"] = new(
                            Type: "string",
                            Description: "Nome del prodotto software di sicurezza (corrispondenza esatta richiesta)")
                    },
                    Required: ["nomeProdotto"])),
            
            new Records.MCPToolDefinition(
                Name: "VerificaCompatibilitaMinaccia",
                Description: "Verifica quali prodotti di sicurezza possono gestire una specifica firma o nome di minaccia.",
                InputSchema: new Records.MCPInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, Records.MCPParameterSchema>
                    {
                        ["nomeMinaccia"] = new(
                            Type: "string",
                            Description: "Nome o firma della minaccia da verificare")
                    },
                    Required: ["nomeMinaccia"]))
        ];
    }
    
    public Records.MCPToolResult SearchSecuritySoftware(Dictionary<string, object> parameters)
    {
        if (!parameters.TryGetValue("tipoSoftware", out object? typeObj))
        {
            return new Records.MCPToolResult(true, null, "Parametro 'tipoSoftware' mancante");
        }
        
        string softwareType = typeObj?.ToString() ?? "";
        parameters.TryGetValue("tipoMinaccia", out object? threatObj);
        string? threatType = threatObj?.ToString();
        
        IEnumerable<SecurityProduct> results = catalog
            .Where(p => p.Type.Equals(softwareType, StringComparison.OrdinalIgnoreCase))
            .Where(p => p.Available)
            .Where(p => threatType == null || p.ProtectsAgainst.Contains(threatType))
            .OrderBy(p => p.Price)
            .Take(5);
        
        if (!results.Any())
        {
            return new Records.MCPToolResult(
                false,
                $"Nessun {softwareType} trovato con i criteri specificati",
                null);
        }
        
        IEnumerable<string> formatted = results.Select(p =>
            $"• {p.Name}: €{p.Price:F2}/anno\n" +
            $"  Protezione: {string.Join(", ", p.ProtectsAgainst)}\n" +
            $"  Features: {string.Join(", ", p.Features)}\n" +
            $"  Disponibile: Sì");
        
        string content = $"Trovati {results.Count()} {softwareType} compatibili:\n\n" +
                        string.Join("\n\n", formatted);
        
        return new Records.MCPToolResult(false, content, null);
    }
    
    public Records.MCPToolResult GetSecurityDetails(Dictionary<string, object> parameters)
    {
        if (!parameters.TryGetValue("nomeProdotto", out object? nameObj))
        {
            return new Records.MCPToolResult(true, null, "Parametro 'nomeProdotto' mancante");
        }
        
        string productName = nameObj?.ToString() ?? "";
        
        SecurityProduct? product = catalog.FirstOrDefault(p =>
            p.Name.Equals(productName, StringComparison.OrdinalIgnoreCase));
        
        if (product == null)
        {
            return new Records.MCPToolResult(
                true,
                null,
                $"Prodotto '{productName}' non trovato nel catalogo");
        }
        
        string content = $"Software: {product.Name}\n" +
                        $"Tipo: {product.Type}\n" +
                        $"Prezzo: €{product.Price:F2}/anno\n" +
                        $"Disponibilità: {(product.Available ? "Disponibile" : "Non disponibile")}\n\n" +
                        $"Protezione contro:\n" +
                        string.Join("\n", product.ProtectsAgainst.Select(t => $"  • {t}")) + "\n\n" +
                        $"Features:\n" +
                        string.Join("\n", product.Features.Select(f => $"  • {f}"));
        
        return new Records.MCPToolResult(false, content, null);
    }
    
    public Records.MCPToolResult CheckThreatCompatibility(Dictionary<string, object> parameters)
    {
        if (!parameters.TryGetValue("nomeMinaccia", out object? threatObj))
        {
            return new Records.MCPToolResult(true, null, "Parametro 'nomeMinaccia' mancante");
        }
        
        string threatName = threatObj?.ToString()?.ToLower() ?? "";
        
        // Map common threat names to threat types
        Dictionary<string, string> threatMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wannacry"] = "ransomware",
            ["petya"] = "ransomware",
            ["cryptolocker"] = "ransomware",
            ["emotet"] = "trojan",
            ["zeus"] = "trojan"
        };
        
        string? threatType = threatMapping.GetValueOrDefault(threatName);
        
        IOrderedEnumerable<SecurityProduct> compatibleProducts = catalog
            .Where(p => p.Available)
            .Where(p => threatType == null || p.ProtectsAgainst.Contains(threatType))
            .OrderBy(p => p.Price);
        
        if (!compatibleProducts.Any())
        {
            return new Records.MCPToolResult(
                false,
                $"Minaccia: {threatName}\nNessun prodotto specifico trovato per questa minaccia. " +
                $"Consigliamo un antivirus con protezione generale.",
                null);
        }
        
        IEnumerable<string> formatted = compatibleProducts.Select((p, i) =>
            $"{i + 1}. {p.Name} (€{p.Price:F2}/anno)\n" +
            $"   - {string.Join(", ", p.Features.Take(2))}");
        
        string content = $"Minaccia: {threatName}\n" +
                        $"Tipo: {threatType ?? "generico"}\n\n" +
                        $"Prodotti compatibili che gestiscono questa minaccia:\n\n" +
                        string.Join("\n\n", formatted) + "\n\n" +
                        $"Raccomandazione: Tutti i prodotti listati offrono protezione efficace.";
        
        return new Records.MCPToolResult(false, content, null);
    }
    
    private record SecurityProduct(
        string Name,
        string Type,
        decimal Price,
        string[] ProtectsAgainst,
        string[] Features,
        bool Available);
}