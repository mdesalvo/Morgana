using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Servers;

/// <summary>
/// Mock MCP server providing hardware component catalog.
/// Uses SQLite for persistence (example implementation).
/// 
/// IMPORTANT: This is a MOCK/EXAMPLE implementation for demonstration purposes.
/// In production environments, customers should:
/// - Connect to real inventory systems (ERP, CRM, databases)
/// - Implement proper authentication and authorization
/// - Add caching and performance optimizations
/// - Handle concurrent access appropriately
/// </summary>
public class HardwareCatalogMCPServer : MorganaMCPServer
{
    private readonly string dbPath;
    
    public HardwareCatalogMCPServer(
        Records.MCPServerConfig config,
        ILogger<HardwareCatalogMCPServer> logger) : base(config, logger)
    {
        dbPath = config.ConnectionString;
        
        // Copy embedded database to disk if not exists
        EnsureDatabaseFromEmbeddedResource("Morgana.AI.Servers.Data.hardware_catalog.db");
        
        logger.LogInformation($"HardwareCatalogMCPServer initialized: {dbPath}");
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
                Description: "Ottieni specifiche dettagliate, prezzi e disponibilità per un modello hardware specifico. " +
                            "Utile per fornire informazioni dettagliate sul prodotto agli utenti.",
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
    
    protected override async Task<Records.MCPToolResult> ExecuteToolAsync(
        string toolName,
        Dictionary<string, object> parameters)
    {
        return toolName switch
        {
            "CercaHardwareCompatibile" => await SearchCompatibleHardwareAsync(parameters),
            "OttieniSpecificheHardware" => await GetHardwareSpecsAsync(parameters),
            _ => new Records.MCPToolResult(true, null, $"Tool sconosciuto: {toolName}")
        };
    }
    
    private async Task<Records.MCPToolResult> SearchCompatibleHardwareAsync(
        Dictionary<string, object> parameters)
    {
        string componentType = parameters["tipoComponente"].ToString()!;
        string? currentModel = parameters.GetValueOrDefault("modelloAttuale")?.ToString();
        string? issueType = parameters.GetValueOrDefault("tipoProblema")?.ToString();
        
        using SqliteConnection connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"
            SELECT model, price, specs, in_stock, recommended_for
            FROM hardware_components
            WHERE type = $type
            AND in_stock = 1
            AND ($issue IS NULL OR recommended_for LIKE '%' || $issue || '%')
            AND ($model IS NULL OR compatible_with LIKE '%' || $model || '%' OR compatible_with = '*')
            ORDER BY price ASC
            LIMIT 5";
        
        command.Parameters.AddWithValue("$type", componentType);
        command.Parameters.AddWithValue("$model", currentModel ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$issue", issueType ?? (object)DBNull.Value);
        
        List<string> results = [];
        using SqliteDataReader reader = await command.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            string model = reader.GetString(0);
            decimal price = reader.GetDecimal(1);
            string specs = reader.GetString(2);
            bool inStock = reader.GetBoolean(3);
            
            // Parse specs JSON
            Dictionary<string, object>? specsObj = JsonSerializer.Deserialize<Dictionary<string, object>>(specs);
            string specsFormatted = string.Join(", ", 
                specsObj?.Select(kvp => $"{kvp.Key}: {kvp.Value}") ?? []);
            
            results.Add($"• {model}: €{price:F2}\n  Specs: {specsFormatted}\n  Available: {(inStock ? "Yes" : "No")}");
        }
        
        string content = results.Count > 0
            ? $"Found {results.Count} compatible {componentType}(s):\n\n{string.Join("\n\n", results)}"
            : $"No compatible {componentType} found matching criteria";
        
        return new Records.MCPToolResult(false, content, null);
    }
    
    private async Task<Records.MCPToolResult> GetHardwareSpecsAsync(
        Dictionary<string, object> parameters)
    {
        string model = parameters["modello"].ToString()!;
        
        using SqliteConnection connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        
        SqliteCommand command = connection.CreateCommand();
        command.CommandText = @"
            SELECT type, price, specs, in_stock, compatible_with
            FROM hardware_components
            WHERE model = $model";
        
        command.Parameters.AddWithValue("$model", model);
        
        using SqliteDataReader reader = await command.ExecuteReaderAsync();
        
        if (await reader.ReadAsync())
        {
            string type = reader.GetString(0);
            decimal price = reader.GetDecimal(1);
            string specs = reader.GetString(2);
            bool inStock = reader.GetBoolean(3);
            string compatibleWith = reader.GetString(4);
            
            Dictionary<string, object>? specsObj = JsonSerializer.Deserialize<Dictionary<string, object>>(specs);
            string specsFormatted = string.Join("\n", 
                specsObj?.Select(kvp => $"  • {kvp.Key}: {kvp.Value}") ?? []);
            
            string content = $@"Hardware: {model}
Type: {type}
Price: €{price:F2}
Available: {(inStock ? "Disponibile" : "Non disponibile")}

Specifications:
{specsFormatted}

Compatible with: {compatibleWith}";
            
            return new Records.MCPToolResult(false, content, null);
        }
        
        return new Records.MCPToolResult(true, null, $"Hardware model '{model}' not found in catalog");
    }
}