using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Examples.Servers;

/// <summary>
/// Mock MCP server providing hardware component catalog (in-memory).
/// 
/// IMPORTANT: This is a MOCK/EXAMPLE implementation for demonstration purposes.
/// In production environments, customers should:
/// - Connect to real inventory systems (ERP, CRM, databases)
/// - Implement proper authentication and authorization
/// - Add caching and performance optimizations
/// </summary>
public class HardwareCatalogMCPServer : MorganaMCPServer
{
    private readonly List<HardwareComponent> catalog;
    
    public HardwareCatalogMCPServer(
        Records.MCPServerConfig config,
        ILogger<HardwareCatalogMCPServer> logger,
        IConfiguration configuration) : base(config, logger, configuration)
    {
        // Initialize in-memory catalog
        catalog =
        [
            // Routers
            new HardwareComponent(
                Type: "router",
                Model: "FiberMax Pro",
                Price: 89.99m,
                Specs: new Dictionary<string, string> { ["speed"] = "1Gbps", ["wifi"] = "WiFi 6", ["ports"] = "4", ["mesh"] = "false" },
                CompatibleWith: ["old_router_x", "basic_router"],
                InStock: true,
                RecommendedFor: ["connessione_lenta", "stabilita"]),

            new HardwareComponent(
                Type: "router",
                Model: "UltraNet 5G",
                Price: 149.99m,
                Specs: new Dictionary<string, string> { ["speed"] = "5Gbps", ["wifi"] = "WiFi 6E", ["ports"] = "8", ["mesh"] = "true" },
                CompatibleWith: ["*"],
                InStock: true,
                RecommendedFor: ["connessione_lenta", "copertura_wifi", "stabilita"]),

            new HardwareComponent(
                Type: "router",
                Model: "GamerEdge Pro",
                Price: 199.99m,
                Specs: new Dictionary<string, string> { ["speed"] = "2.5Gbps", ["wifi"] = "WiFi 6E", ["ports"] = "6", ["qos"] = "gaming" },
                CompatibleWith: ["*"],
                InStock: true,
                RecommendedFor: ["connessione_lenta", "stabilita"]),

            // Modems
            new HardwareComponent(
                Type: "modem",
                Model: "SpeedLink 5G",
                Price: 129.99m,
                Specs: new Dictionary<string, string> { ["speed"] = "5Gbps", ["ports"] = "4", ["docsis"] = "3.1" },
                CompatibleWith: ["router_y", "ultranet", "fibermax"],
                InStock: true,
                RecommendedFor: ["connessione_lenta"]),

            new HardwareComponent(
                Type: "modem",
                Model: "HyperFiber Max",
                Price: 169.99m,
                Specs: new Dictionary<string, string> { ["speed"] = "10Gbps", ["ports"] = "2", ["docsis"] = "4.0" },
                CompatibleWith: ["*"],
                InStock: true,
                RecommendedFor: ["connessione_lenta"]),

            // Extenders
            new HardwareComponent(
                Type: "extender",
                Model: "SignalBoost Mini",
                Price: 39.99m,
                Specs: new Dictionary<string, string> { ["coverage"] = "100m2", ["wifi"] = "WiFi 6", ["mesh"] = "false" },
                CompatibleWith: ["*"],
                InStock: true,
                RecommendedFor: ["copertura_wifi"]),

            new HardwareComponent(
                Type: "extender",
                Model: "MegaRange Pro",
                Price: 79.99m,
                Specs: new Dictionary<string, string> { ["coverage"] = "250m2", ["wifi"] = "WiFi 6E", ["mesh"] = "true" },
                CompatibleWith: ["*"],
                InStock: true,
                RecommendedFor: ["copertura_wifi"]),

            new HardwareComponent(
                Type: "extender",
                Model: "PowerBeam Ultra",
                Price: 119.99m,
                Specs: new Dictionary<string, string> { ["coverage"] = "350m2", ["wifi"] = "WiFi 6E", ["mesh"] = "true", ["outdoor"] = "true" },
                CompatibleWith: ["*"],
                InStock: true,
                RecommendedFor: ["copertura_wifi"])
        ];
        
        logger.LogInformation($"HardwareCatalogMCPServer initialized with {catalog.Count} products in-memory");
    }
    
    protected override Task<IEnumerable<Records.MCPToolDefinition>> RegisterToolsAsync()
    {
        Records.MCPToolDefinition[] tools =
        [
            new Records.MCPToolDefinition(
                Name: "CercaHardwareCompatibile",
                Description: "Cerca componenti hardware compatibili con un modello specifico o tipo di problema. " +
                            "Restituisce lista di prodotti disponibili con specifiche e prezzi.",
                InputSchema: new Records.MCPInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, Records.MCPParameterSchema>
                    {
                        ["tipoComponente"] = new(
                            Type: "string",
                            Description: "Tipo di componente hardware da cercare",
                            Enum: ["router", "modem", "extender"]),
                        ["modelloAttuale"] = new(
                            Type: "string",
                            Description: "Modello hardware attuale per verificare compatibilità (opzionale)",
                            Default: null),
                        ["tipoProblema"] = new(
                            Type: "string",
                            Description: "Tipo di problema che l'utente sta riscontrando (filtro opzionale)",
                            Enum: ["connessione_lenta", "copertura_wifi", "stabilita"],
                            Default: null)
                    },
                    Required: ["tipoComponente"])),
            
            new Records.MCPToolDefinition(
                Name: "OttieniSpecificheHardware",
                Description: "Ottieni specifiche dettagliate, prezzi e disponibilità per un modello hardware specifico.",
                InputSchema: new Records.MCPInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, Records.MCPParameterSchema>
                    {
                        ["modello"] = new(
                            Type: "string",
                            Description: "Identificativo del modello hardware (corrispondenza esatta richiesta)")
                    },
                    Required: ["modello"]))
        ];
        
        return Task.FromResult<IEnumerable<Records.MCPToolDefinition>>(tools);
    }
    
    protected override Task<Records.MCPToolResult> ExecuteToolAsync(
        string toolName,
        Dictionary<string, object> parameters)
    {
        return toolName switch
        {
            "CercaHardwareCompatibile" => SearchCompatibleHardwareAsync(parameters),
            "OttieniSpecificheHardware" => GetHardwareSpecsAsync(parameters),
            _ => Task.FromResult(new Records.MCPToolResult(true, null, $"Tool sconosciuto: {toolName}"))
        };
    }
    
    private Task<Records.MCPToolResult> SearchCompatibleHardwareAsync(Dictionary<string, object> parameters)
    {
        // Use TryGetNormalizedParameter for LLM-tolerant parameter extraction
        if (!TryGetNormalizedParameter(parameters, "tipoComponente", out object? componentTypeObj))
        {
            return Task.FromResult(new Records.MCPToolResult(true, null, "Parametro 'tipoComponente' mancante"));
        }
        
        string componentType = componentTypeObj?.ToString() ?? "";
        
        TryGetNormalizedParameter(parameters, "modelloAttuale", out object? currentModelObj);
        string? currentModel = currentModelObj?.ToString();
        
        TryGetNormalizedParameter(parameters, "tipoProblema", out object? issueTypeObj);
        string? issueType = issueTypeObj?.ToString();
        
        // Query in-memory catalog
        IEnumerable<HardwareComponent> results = catalog
            .Where(c => c.Type == componentType)
            .Where(c => c.InStock)
            .Where(c => issueType == null || c.RecommendedFor.Contains(issueType))
            .Where(c => currentModel == null || 
                        c.CompatibleWith.Contains("*") || 
                        c.CompatibleWith.Any(compat => compat.Contains(currentModel, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(c => c.Price)
            .Take(5);
        
        List<string> formatted = [];
        foreach (HardwareComponent hw in results)
        {
            string specsStr = string.Join(", ", hw.Specs.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
            formatted.Add($"• {hw.Model}: €{hw.Price:F2}\n  Specs: {specsStr}\n  Disponibile: Sì");
        }
        
        string content = formatted.Count > 0
            ? $"Trovati {formatted.Count} {componentType} compatibili:\n\n{string.Join("\n\n", formatted)}"
            : $"Nessun {componentType} compatibile trovato con i criteri specificati";
        
        return Task.FromResult(new Records.MCPToolResult(false, content, null));
    }
    
    private Task<Records.MCPToolResult> GetHardwareSpecsAsync(Dictionary<string, object> parameters)
    {
        // Use TryGetNormalizedParameter - handles "modello", "Modello", etc.
        if (!TryGetNormalizedParameter(parameters, "modello", out object? modelObj))
        {
            logger.LogWarning($"Parameter 'modello' not found. Available parameters: {string.Join(", ", parameters.Keys)}");
            return Task.FromResult(new Records.MCPToolResult(true, null, "Parametro 'modello' mancante"));
        }
        
        string model = modelObj?.ToString() ?? "";
        
        HardwareComponent? hw = catalog.FirstOrDefault(c => 
            c.Model.Equals(model, StringComparison.OrdinalIgnoreCase));
        
        if (hw == null)
        {
            return Task.FromResult(new Records.MCPToolResult(true, null, $"Modello hardware '{model}' non trovato nel catalogo"));
        }
        
        string specsFormatted = string.Join("\n", hw.Specs.Select(kvp => $"  • {kvp.Key}: {kvp.Value}"));
        
        string compatibleWith = hw.CompatibleWith.Contains("*") 
            ? "Tutti i modelli" 
            : string.Join(", ", hw.CompatibleWith);
        
        string content = $@"Hardware: {hw.Model}
Tipo: {hw.Type}
Prezzo: €{hw.Price:F2}
Disponibilità: {(hw.InStock ? "Disponibile" : "Non disponibile")}

Specifiche:
{specsFormatted}

Compatibile con: {compatibleWith}";
        
        return Task.FromResult(new Records.MCPToolResult(false, content, null));
    }
    
    // Internal model
    private record HardwareComponent(
        string Type,
        string Model,
        decimal Price,
        Dictionary<string, string> Specs,
        List<string> CompatibleWith,
        bool InStock,
        List<string> RecommendedFor);
}