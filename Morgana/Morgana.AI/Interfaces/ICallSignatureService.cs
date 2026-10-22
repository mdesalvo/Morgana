using Microsoft.Extensions.AI;

namespace Morgana.AI.Interfaces;

/// <summary>
/// Stands between the model and every tool: holds each call to the schema that the model was offered for its tool, so a
/// call that breaks it never reaches the tool and the model reads the argument at fault.
/// </summary>
/// <remarks>
/// A value written in another form of the type that its parameter declares, such as <c>"true"</c> for a boolean or an object
/// written as JSON text, is the same value and is read as that type without asking the model again. Any other mismatch is a
/// fault: one fact that names one argument and what is wrong with it, worded so that the model can repair the call from it alone.
/// </remarks>
public interface ICallSignatureService
{
    /// <summary>
    /// Rewrites in place every argument that carries, in another form, a value of the type that its parameter
    /// declares; a conversion that could lose or guess part of the value is never made.
    /// </summary>
    /// <param name="context">The call about to run, with every value that the framework binds already written in.</param>
    /// <returns>The parameters whose values were rewritten, so that each slip of the model stays visible to the operator.</returns>
    IReadOnlyList<string> NormalizeArguments(FunctionInvocationContext context);

    /// <summary>The fact that keeps a call from reaching its tool, naming the first argument at fault; null when the call can reach it.</summary>
    /// <param name="context">The call about to run, with every value that the framework binds already written in.</param>
    string? Fault(FunctionInvocationContext context);

    /// <summary>The parameters that the call's schema requires and that its arguments still leave without a value.</summary>
    /// <param name="context">The call about to run, with every value that the framework binds already written in.</param>
    IReadOnlyList<string> MissingValues(FunctionInvocationContext context);
}
