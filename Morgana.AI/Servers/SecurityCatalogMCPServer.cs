using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Servers;

/// <summary>
/// Mock MCP server providing security software catalog (in-memory).
/// 
/// IMPORTANT: This is a MOCK/EXAMPLE implementation for demonstration purposes.
/// In production environments, customers should:
/// - Connect to real security product databases
/// - Integrate with threat intelligence feeds
/// - Implement proper licensing and compliance checks
/// </summary>
public class SecurityCatalogMCPServer : MorganaMCPServer
{
    private readonly List<SecurityProduct> catalog;
    
    public SecurityCatalogMCPServer(
        Records.MCPServerConfig config,
        ILogger<SecurityCatalogMCPServer> logger) : base(config, logger)
    {
        // Initialize in-memory catalog
        catalog =
        [
            // Antivirus
            new SecurityProduct(
                Type: "antivirus",
                ProductName: "SecureShield Free",
                Vendor: "ShieldTech",
                Price: 0m,
                Features: new Dictionary<string, string> { ["realtime"] = "yes", ["cloud"] = "no", ["auto_update"] = "yes", ["web_protection"] = "basic" },
                ThreatCoverage: ["virus", "trojan", "worm"],
                RecommendedFor: ["prestazioni_lente", "popup_pubblicitari"],
                SystemRequirements: "Windows 10+, 2GB RAM",
                Rating: 4),

            new SecurityProduct(
                Type: "antivirus",
                ProductName: "TotalDefender Pro",
                Vendor: "DefenderCorp",
                Price: 49.99m,
                Features: new Dictionary<string, string> { ["realtime"] = "yes", ["cloud"] = "yes", ["auto_update"] = "yes", ["web_protection"] = "advanced", ["ransomware_shield"] = "yes" },
                ThreatCoverage: ["virus", "trojan", "worm", "ransomware", "malware", "adware"],
                RecommendedFor: ["prestazioni_lente", "popup_pubblicitari", "redirect_browser"],
                SystemRequirements: "Windows 10+, 4GB RAM",
                Rating: 5),

            new SecurityProduct(
                Type: "antivirus",
                ProductName: "UltraSafe Premium",
                Vendor: "SecureSoft",
                Price: 79.99m,
                Features: new Dictionary<string, string> { ["realtime"] = "yes", ["cloud"] = "yes", ["auto_update"] = "yes", ["web_protection"] = "advanced", ["ransomware_shield"] = "yes", ["ai_detection"] = "yes" },
                ThreatCoverage: ["virus", "trojan", "worm", "ransomware", "malware", "adware", "spyware", "rootkit"],
                RecommendedFor: ["prestazioni_lente", "popup_pubblicitari", "redirect_browser", "abuso_rete"],
                SystemRequirements: "Windows 10+, 8GB RAM",
                Rating: 5),

            // Anti-malware
            new SecurityProduct(
                Type: "anti-malware",
                ProductName: "MalwareKiller Free",
                Vendor: "CleanTech",
                Price: 0m,
                Features: new Dictionary<string, string> { ["scan_on_demand"] = "yes", ["quarantine"] = "yes", ["scheduled_scan"] = "no" },
                ThreatCoverage: ["malware", "adware", "pup"],
                RecommendedFor: ["popup_pubblicitari", "redirect_browser"],
                SystemRequirements: "Windows 10+, 2GB RAM",
                Rating: 3),

            new SecurityProduct(
                Type: "anti-malware",
                ProductName: "DeepClean Pro",
                Vendor: "CleanTech",
                Price: 39.99m,
                Features: new Dictionary<string, string> { ["scan_on_demand"] = "yes", ["quarantine"] = "yes", ["scheduled_scan"] = "yes", ["rootkit_scan"] = "yes" },
                ThreatCoverage: ["malware", "adware", "pup", "rootkit", "spyware"],
                RecommendedFor: ["popup_pubblicitari", "redirect_browser", "prestazioni_lente"],
                SystemRequirements: "Windows 10+, 4GB RAM",
                Rating: 4),

            // Firewall
            new SecurityProduct(
                Type: "firewall",
                ProductName: "NetGuard Basic",
                Vendor: "NetSecure",
                Price: 0m,
                Features: new Dictionary<string, string> { ["bidirectional"] = "yes", ["application_control"] = "basic", ["intrusion_detection"] = "no" },
                ThreatCoverage: ["network_attacks", "port_scan"],
                RecommendedFor: ["abuso_rete"],
                SystemRequirements: "Windows 10+, 1GB RAM",
                Rating: 3),

            new SecurityProduct(
                Type: "firewall",
                ProductName: "FirewallPro Advanced",
                Vendor: "NetSecure",
                Price: 59.99m,
                Features: new Dictionary<string, string> { ["bidirectional"] = "yes", ["application_control"] = "advanced", ["intrusion_detection"] = "yes", ["geo_blocking"] = "yes" },
                ThreatCoverage: ["network_attacks", "port_scan", "ddos", "intrusion"],
                RecommendedFor: ["abuso_rete"],
                SystemRequirements: "Windows 10+, 4GB RAM",
                Rating: 5),

            // VPN
            new SecurityProduct(
                Type: "vpn",
                ProductName: "PrivacyVPN Free",
                Vendor: "VPNGlobal",
                Price: 0m,
                Features: new Dictionary<string, string> { ["servers"] = "10", ["bandwidth"] = "10GB/month", ["encryption"] = "AES-128", ["kill_switch"] = "no" },
                ThreatCoverage: ["tracking", "geo_restriction"],
                RecommendedFor: ["redirect_browser"],
                SystemRequirements: "Windows 10+, 1GB RAM",
                Rating: 3),

            new SecurityProduct(
                Type: "vpn",
                ProductName: "SecureVPN Premium",
                Vendor: "VPNGlobal",
                Price: 89.99m,
                Features: new Dictionary<string, string> { ["servers"] = "100", ["bandwidth"] = "unlimited", ["encryption"] = "AES-256", ["kill_switch"] = "yes", ["ad_blocker"] = "yes" },
                ThreatCoverage: ["tracking", "geo_restriction", "isp_throttling"],
                RecommendedFor: ["redirect_browser", "abuso_rete"],
                SystemRequirements: "Windows 10+, 2GB RAM",
                Rating: 5)
        ];
        
        logger.LogInformation($"SecurityCatalogMCPServer initialized with {catalog.Count} products in-memory");
    }
    
    protected override Task<IEnumerable<Records.MCPToolDefinition>> RegisterToolsAsync()
    {
        Records.MCPToolDefinition[] tools =
        [
            new Records.MCPToolDefinition(
                Name: "CercaSoftwareSicurezza",
                Description: "Cerca software antivirus e di sicurezza in base al tipo di minaccia o sintomi osservati. " +
                            "Restituisce prodotti consigliati con funzionalità e prezzi.",
                InputSchema: new Records.MCPInputSchema(
                    Type: "object",
                    Properties: new Dictionary<string, Records.MCPParameterSchema>
                    {
                        ["tipoSoftware"] = new(
                            Type: "string",
                            Description: "Tipo di software di sicurezza necessario",
                            Enum: ["antivirus", "firewall", "vpn", "anti-malware"]),
                        ["tipoMinaccia"] = new(
                            Type: "string",
                            Description: "Tipo di minaccia rilevata (filtro opzionale)",
                            Enum: ["virus", "malware", "ransomware", "adware", "sconosciuto"],
                            Default: null),
                        ["sintomi"] = new(
                            Type: "string",
                            Description: "Sintomi osservati dall'utente (filtro opzionale)",
                            Enum: ["prestazioni_lente", "popup_pubblicitari", "redirect_browser", "abuso_rete"],
                            Default: null)
                    },
                    Required: ["tipoSoftware"])),
            
            new Records.MCPToolDefinition(
                Name: "OttieniDettagliSoftwareSicurezza",
                Description: "Ottieni informazioni dettagliate su un prodotto software di sicurezza specifico.",
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
                            Description: "Nome o firma della minaccia rilevata")
                    },
                    Required: ["nomeMinaccia"]))
        ];
        
        return Task.FromResult<IEnumerable<Records.MCPToolDefinition>>(tools);
    }
    
    protected override Task<Records.MCPToolResult> ExecuteToolAsync(
        string toolName,
        Dictionary<string, object> parameters)
    {
        return toolName switch
        {
            "CercaSoftwareSicurezza" => SearchSecuritySoftwareAsync(parameters),
            "OttieniDettagliSoftwareSicurezza" => GetSecurityDetailsAsync(parameters),
            "VerificaCompatibilitaMinaccia" => CheckThreatCompatibilityAsync(parameters),
            _ => Task.FromResult(new Records.MCPToolResult(true, null, $"Tool sconosciuto: {toolName}"))
        };
    }
    
    private Task<Records.MCPToolResult> SearchSecuritySoftwareAsync(Dictionary<string, object> parameters)
    {
        string softwareType = parameters["tipoSoftware"].ToString()!;
        string? threatType = parameters.GetValueOrDefault("tipoMinaccia")?.ToString();
        string? symptoms = parameters.GetValueOrDefault("sintomi")?.ToString();
        
        // Query in-memory catalog with scoring system
        var scoredResults = catalog
            .Where(p => p.Type == softwareType)
            .Select(p => new
            {
                Product = p,
                Score = CalculateRelevanceScore(p, threatType, symptoms)
            })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Product.Rating)
            .Take(5)
            .Select(x => x.Product);
        
        List<string> formatted = [];
        foreach (SecurityProduct prod in scoredResults)
        {
            string featuresStr = string.Join(", ", prod.Features.Take(3).Select(kvp => $"{kvp.Key}: {kvp.Value}"));
            string priceStr = prod.Price == 0 ? "Gratuito" : $"€{prod.Price:F2}/anno";
            formatted.Add($"• {prod.ProductName} ({prod.Vendor})\n  Prezzo: {priceStr}\n  Features: {featuresStr}");
        }
        
        string content = formatted.Count > 0
            ? $"Trovate {formatted.Count} soluzioni {softwareType}:\n\n{string.Join("\n\n", formatted)}"
            : $"Nessuna soluzione {softwareType} trovata. Prova senza filtri aggiuntivi.";
        
        return Task.FromResult(new Records.MCPToolResult(false, content, null));
    }
    
    // Helper method to score product relevance
    private int CalculateRelevanceScore(SecurityProduct product, string? threatType, string? symptoms)
    {
        int score = 10; // Base score for matching software type
        
        // Bonus for threat coverage match
        if (!string.IsNullOrEmpty(threatType))
        {
            if (product.ThreatCoverage.Any(t => t.Equals(threatType, StringComparison.OrdinalIgnoreCase)))
                score += 50; // Exact match
            else if (product.ThreatCoverage.Any(t => t.Contains(threatType, StringComparison.OrdinalIgnoreCase)))
                score += 30; // Partial match
        }
        
        // Bonus for symptom match
        if (!string.IsNullOrEmpty(symptoms))
        {
            if (product.RecommendedFor.Contains(symptoms, StringComparer.OrdinalIgnoreCase))
                score += 40; // Exact match
        }
        
        // Bonus for rating
        score += product.Rating * 5;
        
        return score;
    }
    
    private Task<Records.MCPToolResult> GetSecurityDetailsAsync(Dictionary<string, object> parameters)
    {
        string productName = parameters["nomeProdotto"].ToString()!;
        
        SecurityProduct? prod = catalog.FirstOrDefault(p => 
            p.ProductName.Equals(productName, StringComparison.OrdinalIgnoreCase));
        
        if (prod == null)
        {
            return Task.FromResult(new Records.MCPToolResult(true, null, $"Prodotto '{productName}' non trovato nel catalogo"));
        }
        
        string featuresFormatted = string.Join("\n", prod.Features.Select(kvp => $"  • {kvp.Key}: {kvp.Value}"));
        
        string priceStr = prod.Price == 0 ? "Gratuito" : $"€{prod.Price:F2}/anno";
        string threatsStr = string.Join(", ", prod.ThreatCoverage);
        
        string content = $@"Prodotto: {prod.ProductName}
Vendor: {prod.Vendor}
Tipo: {prod.Type}
Prezzo: {priceStr}

Funzionalità:
{featuresFormatted}

Copertura minacce: {threatsStr}
Requisiti di sistema: {prod.SystemRequirements}";
        
        return Task.FromResult(new Records.MCPToolResult(false, content, null));
    }
    
    private Task<Records.MCPToolResult> CheckThreatCompatibilityAsync(Dictionary<string, object> parameters)
    {
        string threatName = parameters["nomeMinaccia"].ToString()!;
        
        List<SecurityProduct> compatibleProducts = catalog
            .Where(p => p.ThreatCoverage.Any(t => t.Contains(threatName, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.Rating)
            .ToList();
        
        if (compatibleProducts.Count == 0)
        {
            return Task.FromResult(new Records.MCPToolResult(
                false, 
                $"Nessun prodotto di sicurezza trovato con copertura per '{threatName}'", 
                null));
        }
        
        List<string> formatted = compatibleProducts
            .Select(p => $"• {p.ProductName} di {p.Vendor}")
            .ToList();
        
        string content = $"Prodotti che possono gestire '{threatName}':\n{string.Join("\n", formatted)}";
        
        return Task.FromResult(new Records.MCPToolResult(false, content, null));
    }
    
    // Internal model
    private record SecurityProduct(
        string Type,
        string ProductName,
        string Vendor,
        decimal Price,
        Dictionary<string, string> Features,
        List<string> ThreatCoverage,
        List<string> RecommendedFor,
        string SystemRequirements,
        int Rating);
}