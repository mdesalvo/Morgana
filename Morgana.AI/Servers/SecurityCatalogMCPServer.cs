using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Servers;

/// <summary>
/// Mock MCP server providing security software catalog.
/// Complements hardware catalog for comprehensive troubleshooting.
/// 
/// IMPORTANT: This is a MOCK/EXAMPLE implementation for demonstration purposes.
/// In production environments, customers should:
/// - Connect to real security product databases
/// - Integrate with threat intelligence feeds
/// - Implement proper licensing and compliance checks
/// - Add vendor API integrations for real-time pricing
/// </summary>
public class SecurityCatalogMCPServer : MorganaMCPServer
{
    private readonly string dbPath;
    
    public SecurityCatalogMCPServer(
        Records.MCPServerConfig config,
        ILogger<SecurityCatalogMCPServer> logger) : base(config, logger)
    {
        dbPath = config.ConnectionString;
        
        // Copy embedded database to disk if not exists
        EnsureDatabaseFromEmbeddedResource("Morgana.AI.Servers.Data.security_catalog.db");
        
        logger.LogInformation($"SecurityCatalogMCPServer initialized: {dbPath}");
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
                Description: "Ottieni informazioni dettagliate su un prodotto software di sicurezza specifico. " +
                            "Include funzionalità, copertura minacce e requisiti di sistema.",
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
                Description: "Verifica quali prodotti di sicurezza possono gestire una specifica firma o nome di minaccia. " +
                            "Utile quando viene rilevato un malware/virus specifico.",
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
    
    protected override async Task<Records.MCPToolResult> ExecuteToolAsync(
        string toolName,
        Dictionary<string, object> parameters)
    {
        return toolName switch
        {
            "CercaSoftwareSicurezza" => await SearchSecuritySoftwareAsync(parameters),
            "OttieniDettagliSoftwareSicurezza" => await GetSecurityDetailsAsync(parameters),
            "VerificaCompatibilitaMinaccia" => await CheckThreatCompatibilityAsync(parameters),
            _ => new Records.MCPToolResult(true, null, $"Tool sconosciuto: {toolName}")
        };
    }
    
    private async Task<Records.MCPToolResult> SearchSecuritySoftwareAsync(Dictionary<string, object> parameters)
    {
        string softwareType = parameters["tipoSoftware"].ToString()!;
        string? threatType = parameters.GetValueOrDefault("tipoMinaccia")?.ToString();
        string? symptoms = parameters.GetValueOrDefault("sintomi")?.ToString();
        
        using SqliteConnection connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"
            SELECT product_name, vendor, price, features, threat_coverage
            FROM security_products
            WHERE type = $type
            AND ($threat IS NULL OR threat_coverage LIKE '%' || $threat || '%')
            AND ($symptom IS NULL OR recommended_for LIKE '%' || $symptom || '%')
            ORDER BY rating DESC
            LIMIT 5";
        
        command.Parameters.AddWithValue("$type", softwareType);
        command.Parameters.AddWithValue("$threat", threatType ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$symptom", symptoms ?? (object)DBNull.Value);
        
        List<string> results = [];
        using SqliteDataReader reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            string product = reader.GetString(0);
            string vendor = reader.GetString(1);
            decimal price = reader.GetDecimal(2);
            string features = reader.GetString(3);
            
            Dictionary<string, object>? featuresObj = JsonSerializer.Deserialize<Dictionary<string, object>>(features);
            string featuresFormatted = string.Join(", ",
                featuresObj?.Select(kvp => $"{kvp.Key}: {kvp.Value}") ?? []);
            
            string priceDisplay = price == 0 ? "Free" : $"€{price:F2}/year";
            
            results.Add($"• {product} ({vendor})\n  Price: {priceDisplay}\n  Features: {featuresFormatted}");
        }
        
        string content = results.Count > 0
            ? $"Found {results.Count} {softwareType} solution(s):\n\n{string.Join("\n\n", results)}"
            : $"No {softwareType} solutions found matching criteria";
        
        return new Records.MCPToolResult(false, content, null);
    }
    
    private async Task<Records.MCPToolResult> GetSecurityDetailsAsync(Dictionary<string, object> parameters)
    {
        string productName = parameters["nomeProdotto"].ToString()!;
        
        using SqliteConnection connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"
            SELECT type, vendor, price, features, threat_coverage, system_requirements
            FROM security_products
            WHERE product_name = $product";
        
        command.Parameters.AddWithValue("$product", productName);
        
        using SqliteDataReader reader = await command.ExecuteReaderAsync();
        
        if (await reader.ReadAsync())
        {
            string type = reader.GetString(0);
            string vendor = reader.GetString(1);
            decimal price = reader.GetDecimal(2);
            string features = reader.GetString(3);
            string threats = reader.GetString(4);
            string requirements = reader.GetString(5);
            
            Dictionary<string, object>? featuresObj = JsonSerializer.Deserialize<Dictionary<string, object>>(features);
            string featuresFormatted = string.Join("\n",
                featuresObj?.Select(kvp => $"  • {kvp.Key}: {kvp.Value}") ?? []);
            
            string priceDisplay = price == 0 ? "Free" : $"€{price:F2}/year";
            
            string content = $@"Product: {productName}
Vendor: {vendor}
Type: {type}
Price: {priceDisplay}

Features:
{featuresFormatted}

Threat Coverage: {threats}
System Requirements: {requirements}";
            
            return new Records.MCPToolResult(false, content, null);
        }
        
        return new Records.MCPToolResult(true, null, $"Security product '{productName}' not found");
    }
    
    private async Task<Records.MCPToolResult> CheckThreatCompatibilityAsync(Dictionary<string, object> parameters)
    {
        string threatName = parameters["nomeMinaccia"].ToString()!;
        
        using SqliteConnection connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"
            SELECT product_name, vendor, threat_coverage
            FROM security_products
            WHERE threat_coverage LIKE '%' || $threat || '%'
            ORDER BY rating DESC";
        
        command.Parameters.AddWithValue("$threat", threatName);
        
        List<string> results = [];
        using SqliteDataReader reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            string product = reader.GetString(0);
            string vendor = reader.GetString(1);
            results.Add($"• {product} by {vendor}");
        }
        
        string content = results.Count > 0
            ? $"Products that can handle '{threatName}':\n{string.Join("\n", results)}"
            : $"No security products found with coverage for '{threatName}'";
        
        return new Records.MCPToolResult(false, content, null);
    }
}