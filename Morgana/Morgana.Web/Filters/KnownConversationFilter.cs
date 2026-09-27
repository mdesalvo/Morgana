using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Morgana.AI.Interfaces;

namespace Morgana.Web.Filters;

/// <summary>Refuses with 404 a call addressing a conversation that was never started.</summary>
/// <param name="conversationPersistenceService">Knows whether the conversation exists on record.</param>
/// <param name="logger">Records the refused call.</param>
public sealed class KnownConversationFilter(
    IConversationPersistenceService conversationPersistenceService,
    ILogger logger) : IAsyncActionFilter
{
    /// <summary>Route value naming the conversation: filters and actions all read it, never the body.</summary>
    public const string ConversationIdRouteKey = "conversationId";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // A missing id is judged like an unknown one: it addresses no conversation on record.
        // So is one start could never have accepted, which is refused before it names a file on disk.
        string conversationId = context.RouteData.Values[ConversationIdRouteKey]?.ToString() ?? string.Empty;
        if (!Guid.TryParseExact(conversationId, "N", out _)
             || !conversationPersistenceService.ConversationExists(conversationId))
        {
            logger.LogWarning("{Action} for unknown conversation {ConversationId}; returning 404",
                context.ActionDescriptor.DisplayName, conversationId);
            context.Result = new NotFoundObjectResult(new { error = "Conversation not found", conversationId });
            return;
        }

        await next();
    }
}