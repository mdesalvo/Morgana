using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Morgana.Web.Filters;

/// <summary>Answers any failure of a REST call, whether in a gate or in the action, with the same 500 body.</summary>
/// <param name="logger">Records the failure with the action and the conversation it was serving.</param>
public sealed class FailureResponseFilter(ILogger logger) : IExceptionFilter
{
    /// <inheritdoc />
    public void OnException(ExceptionContext context)
    {
        // The conversation the route names, when it names one: start carries it in its body alone
        object? conversationId = context.RouteData.Values[KnownConversationFilter.ConversationIdRouteKey];
        logger.LogError(context.Exception, "{Action} failed on conversation {ConversationId}",
            context.ActionDescriptor.DisplayName, conversationId);

        // A channel reads one error shape from every endpoint, whichever step of the request broke
        context.Result = new ObjectResult(new { error = context.Exception.Message }) { StatusCode = StatusCodes.Status500InternalServerError };
        context.ExceptionHandled = true;
    }
}