using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;
using Morgana.AI.Providers;

namespace Morgana.AI.Tools;

public class MorganaTool
{
    protected readonly ILogger<MorganaAgent> logger;
    protected readonly Func<MorganaContextProvider> getContextProvider;

    public MorganaTool(
        ILogger<MorganaAgent> logger,
        Func<MorganaContextProvider> getContextProvider)
    {
        this.logger = logger;
        this.getContextProvider = getContextProvider;
    }

    /// <summary>
    /// Recupera una variabile dal contesto dell'agente via MorganaContextProvider
    /// </summary>
    public Task<object> GetContextVariable(string variableName)
    {
        MorganaContextProvider provider = getContextProvider();
        object? value = provider.GetVariable(variableName);

        if (value != null)
        {
            logger.LogInformation(
                $"MorganaTool ({GetType().Name}) HIT variable '{variableName}' from agent context. Value is: {value}");

            return Task.FromResult(value);
        }

        logger.LogInformation(
            $"MorganaTool ({GetType().Name}) MISS variable '{variableName}' from agent context.");

        return Task.FromResult<object>(
            $"Informazione {variableName} non disponibile nel contesto: devi ingaggiare SetContextVariable per valorizzarla.");
    }

    /// <summary>
    /// Imposta una variabile nel contesto dell'agente via MorganaContextProvider
    /// Se la variabile è shared, il provider notificherà automaticamente RouterActor
    /// </summary>
    public Task<object> SetContextVariable(string variableName, string variableValue)
    {
        MorganaContextProvider provider = getContextProvider();
        provider.SetVariable(variableName, variableValue);

        return Task.FromResult<object>(
            $"Informazione {variableName} inserita nel contesto con valore: {variableValue}");
    }
}