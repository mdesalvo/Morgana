using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Morgana.AI.Interfaces;

namespace Morgana.AI.Services;

/// <summary>
/// Default <see cref="ICallSignatureService"/>: reads the tool's signature from the JSON schema that the model was offered,
/// then holds the call to its required parameters, the type of each value and the values that a parameter lists.
/// </summary>
public class SchemaCallSignatureService : ICallSignatureService
{
    /// <inheritdoc/>
    public IReadOnlyList<string> NormalizeArguments(FunctionInvocationContext context)
    {
        // The types that each parameter takes are the ones that the model was offered.
        Records.CallSignature signature = Records.CallSignature.Of(context.Function.JsonSchema);
        List<string> normalizedParameters = [];

        foreach (string parameter in signature.Parameters.Keys)
        {
            // Only a value as the model wrote it and of a type that its parameter refuses can be one written in another form.
            if (!context.Arguments.TryGetValue(parameter, out object? argument) || argument is not JsonElement value || signature.Accepts(parameter, value))
                continue;

            // A value with no lossless reading as a type that the parameter takes is left for the fault to name.
            if (ElementaryReading(value, signature.Parameters[parameter]) is not { } elementaryValue)
                continue;

            // The value is now in the form that the parameter takes, so the call goes on as if the model had written it so.
            context.Arguments[parameter] = elementaryValue;
            normalizedParameters.Add(parameter);
        }

        // The rewritten parameters are reported, so that the caller can log what the model slipped on.
        return normalizedParameters;
    }

    /// <inheritdoc/>
    public string? Fault(FunctionInvocationContext context)
    {
        // The call is held to the schema that the model was offered for it.
        Records.CallSignature signature = Records.CallSignature.Of(context.Function.JsonSchema);

        // A value that the call requires and does not carry is named first, since there is no value of it to judge.
        if (MissingValues(context, signature) is [string missingParameter, ..])
            return $"the call leaves out '{missingParameter}', which '{context.Function.Name}' requires";

        foreach ((string parameter, object? argument) in context.Arguments)
        {
            // Only a value as the model wrote it is judged, for a parameter that the tool declares.
            if (argument is not JsonElement value || !signature.Parameters.ContainsKey(parameter))
                continue;

            // A value of a type that its parameter refuses is the fault, named with the types that the parameter takes.
            if (!signature.Accepts(parameter, value))
                return signature.MismatchReason("the call", context.Function.Name, parameter, value);

            // A value outside those that its parameter lists is the fault, named with the values that the parameter allows.
            if (!signature.Allows(parameter, value))
                return signature.ChoiceReason("the call", context.Function.Name, parameter, value);
        }

        // Every value reaches its parameter in a form that it takes.
        return null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> MissingValues(FunctionInvocationContext context)
        // The required list is the one that the model was offered, from which the framework drops every context value it fills itself.
        => MissingValues(context, Records.CallSignature.Of(context.Function.JsonSchema));

    /// <summary>
    /// Reads a value as a boolean, an integer or a number that the parameter takes, when the value holds exactly one of
    /// them in another form; null when no such reading exists or one would lose part of the value.
    /// </summary>
    /// <param name="value">The value as the model wrote it.</param>
    /// <param name="acceptedTypes">The JSON types that the parameter takes.</param>
    private static JsonElement? ElementaryReading(JsonElement value, IReadOnlyList<string> acceptedTypes)
    {
        // A number written with a zero fraction, as 2.0, is the integer that an integer parameter takes.
        if (value.ValueKind == JsonValueKind.Number)
            return acceptedTypes.Contains("integer") && value.TryGetDecimal(out decimal writtenNumber) && IsIntegral(writtenNumber)
                ? JsonSerializer.SerializeToElement((long)writtenNumber)
                : null;

        // Every other reading starts from text: an object, a list or a boolean meant for another type has none.
        if (value.ValueKind != JsonValueKind.String)
            return null;
        string text = value.GetString()!.Trim();

        // The two words of a boolean, in any case, are the boolean that a boolean parameter takes.
        if (acceptedTypes.Contains("boolean") && bool.TryParse(text, out bool writtenBoolean))
            return JsonSerializer.SerializeToElement(writtenBoolean);

        // A number written as text is read with the invariant culture, so that "2.5" means the same on every host.
        bool isNumber = decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal textNumber);

        // An integral number, as "2" or "2.0", is the integer that an integer parameter takes.
        if (isNumber && acceptedTypes.Contains("integer") && IsIntegral(textNumber))
            return JsonSerializer.SerializeToElement((long)textNumber);

        // Any number is the number that a number parameter takes; a fraction meant for an integer has no lossless reading.
        if (isNumber && acceptedTypes.Contains("number"))
            return JsonSerializer.SerializeToElement(textNumber);

        // The text holds none of the types that the parameter takes.
        return null;
    }

    /// <summary>Whether a number has no fraction and fits the integers that a JSON integer parameter carries.</summary>
    /// <param name="number">The number as written.</param>
    private static bool IsIntegral(decimal number)
        // A whole number within the range of a long is carried unchanged; any other one would be cut or overflow.
        => number == decimal.Truncate(number) && number is >= long.MinValue and <= long.MaxValue;

    /// <summary>The parameters that the signature requires and that the call's arguments leave without a value.</summary>
    /// <param name="context">The call about to run.</param>
    /// <param name="signature">The signature of the called tool.</param>
    private static List<string> MissingValues(FunctionInvocationContext context, Records.CallSignature signature)
        // A parameter is lacking when the call carries nothing under its name or carries null.
        => [.. signature.Required.Where(name => !context.Arguments.TryGetValue(name, out object? value)
                                                 || value is null
                                                 || value is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined })];
}
