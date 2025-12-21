using Microsoft.Extensions.Logging;
using Morgana.AI.Abstractions;

namespace Morgana.AI.Tools;

public class MorganaTool
{
    protected readonly ILogger<MorganaAgent> logger;

    protected readonly Dictionary<string, object> agentContext;
    protected readonly HashSet<string> sharedContextVariables;
    protected Action<string, object>? onSharedContextUpdate;

    public MorganaTool(
        ILogger<MorganaAgent> logger,
        Dictionary<string, object> agentContext,
        IEnumerable<string>? sharedVariableNames=null)
    {
        this.logger = logger;
        this.agentContext = agentContext;
        sharedContextVariables = [.. sharedVariableNames ?? []];
    }

    public void RegisterSharedContextUpdateCallback(Action<string, object> sharedContextUpdateCallback)
        => onSharedContextUpdate = sharedContextUpdateCallback;

    public Task<object> GetContextVariable(string variableName)
    {
        if (agentContext.TryGetValue(variableName, out object? value))
        {
            logger.LogInformation($"MorganaTool ({GetType().Name}) HIT variable {variableName} from agent context. Value is: {value}");

            return Task.FromResult(value);
        }

        logger.LogInformation($"MorganaTool ({GetType().Name}) MISS variable {variableName} from agent context.");
        return Task.FromResult<object>($"Informazione {variableName} non disponibile nel contesto: devi ingaggiare SetContextVariable per valorizzarla.");
    }

    public Task<object> SetContextVariable(string variableName, string variableValue)
    {
        agentContext[variableName] = variableValue;

        if (sharedContextVariables.Contains(variableName))
        {
            onSharedContextUpdate?.Invoke(variableName, variableValue);

            logger.LogInformation($"MorganaTool ({GetType().Name}) SET SHARED variable {variableName} to agent context. Value is: {variableValue}");
        }
        else
        {
            logger.LogInformation($"MorganaTool ({GetType().Name}) SET PRIVATE variable {variableName} to agent context. Value is: {variableValue}");
        }

        return Task.FromResult<object>($"Informazione {variableName} inserita nel contesto con valore: {variableValue}");
    }
}