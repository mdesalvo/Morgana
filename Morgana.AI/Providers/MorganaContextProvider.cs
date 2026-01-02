using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Morgana.AI.Providers;

/// <summary>
/// AIContextProvider custom che mantiene lo stato delle variabili di contesto dell'agente.
/// Supporta serializzazione/deserializzazione automatica per persistenza con AgentThread.
/// </summary>
public class MorganaContextProvider : AIContextProvider
{
    private readonly ILogger logger;
    private readonly HashSet<string> sharedVariableNames;

    // Source of truth per le variabili di contesto
    public Dictionary<string, object> AgentContext { get; private set; } = new();

    // Callback per notificare aggiornamenti di variabili shared
    public Action<string, object>? OnSharedContextUpdate { get; set; }

    public MorganaContextProvider(
        ILogger logger,
        IEnumerable<string>? sharedVariableNames = null)
    {
        this.logger = logger;
        this.sharedVariableNames = new HashSet<string>(sharedVariableNames ?? []);
    }

    /// <summary>
    /// Recupera una variabile dal contesto dell'agente
    /// </summary>
    public object? GetVariable(string variableName)
    {
        if (AgentContext.TryGetValue(variableName, out object? value))
        {
            logger.LogInformation(
                $"MorganaContextProvider GET '{variableName}' = '{value}'");
            return value;
        }

        logger.LogInformation(
            $"MorganaContextProvider MISS '{variableName}'");
        return null;
    }

    /// <summary>
    /// Imposta una variabile nel contesto dell'agente
    /// Se la variabile è shared, invoca il callback per notificare RouterActor
    /// </summary>
    public void SetVariable(string variableName, object variableValue)
    {
        AgentContext[variableName] = variableValue;

        bool isShared = sharedVariableNames.Contains(variableName);
        
        logger.LogInformation(
            $"MorganaContextProvider SET {(isShared ? "SHARED" : "PRIVATE")} '{variableName}' = '{variableValue}'");

        if (isShared)
        {
            OnSharedContextUpdate?.Invoke(variableName, variableValue);
        }
    }

    /// <summary>
    /// Merge di variabili ricevute da altri agenti (context sync P2P)
    /// Usa strategia first-write-wins: accetta solo variabili non già presenti
    /// </summary>
    public void MergeSharedContext(Dictionary<string, object> sharedContext)
    {
        foreach (KeyValuePair<string, object> kvp in sharedContext)
        {
            if (!AgentContext.TryGetValue(kvp.Key, out object? value))
            {
                AgentContext[kvp.Key] = kvp.Value;

                logger.LogInformation(
                    $"MorganaContextProvider MERGED shared context '{kvp.Key}' = '{kvp.Value}'");
            }
            else
            {
                logger.LogInformation(
                    $"MorganaContextProvider IGNORED shared context '{kvp.Key}' (already set to '{value}')");
            }
        }
    }

    /// <summary>
    /// Serializza lo stato del provider per persistenza con AgentThread
    /// </summary>
    public string Serialize()
    {
        return JsonSerializer.Serialize(new
        {
            AgentContext,
            SharedVariableNames = sharedVariableNames
        });
    }

    /// <summary>
    /// Deserializza lo stato del provider da AgentThread
    /// </summary>
    public static MorganaContextProvider Deserialize(
        string json, 
        ILogger logger)
    {
        Dictionary<string, JsonElement>? data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        
        Dictionary<string, object> agentContext = data?["AgentContext"].Deserialize<Dictionary<string, object>>() 
                                                    ?? new Dictionary<string, object>();
        
        HashSet<string> sharedVars = data?["SharedVariableNames"].Deserialize<HashSet<string>>() ?? [];

        MorganaContextProvider provider = new MorganaContextProvider(logger, sharedVars)
        {
            AgentContext = agentContext
        };

        logger.LogInformation(
            $"MorganaContextProvider DESERIALIZED with {agentContext.Count} variables");

        return provider;
    }

    // Implementazioni AIContextProvider (opzionali per ora)
    public override ValueTask<AIContext> InvokingAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        // Qui potremmo iniettare variabili di contesto come messaggi di sistema
        // se necessario in futuro. Per ora restituiamo un contesto vuoto.
        return ValueTask.FromResult(new AIContext());
    }

    public override ValueTask InvokedAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        // Qui potremmo ispezionare/loggare le risposte dell'agente
        return base.InvokedAsync(context, cancellationToken);
    }
}