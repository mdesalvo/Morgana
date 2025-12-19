namespace Morgana.AI.Tools;

public class MorganaTool
{
    private readonly Dictionary<string, object> Context;

    public MorganaTool(Dictionary<string, object> context)
    {
        Context = context;
    }

    public async Task<object> GetContextVariable(string variableName)
        => Context.TryGetValue(variableName, out object? value)
             ? value : "Variabile non disponibile nel contesto: devi ingaggiare SetContextVariable per valorizzarla.";

    public async Task<object> SetContextVariable(string variableName, string variableValue)
    {
        Context[variableName] = variableValue;
        return $"Variabile {variableName} valorizzata nel contesto con valore: {variableValue}";
    }
}