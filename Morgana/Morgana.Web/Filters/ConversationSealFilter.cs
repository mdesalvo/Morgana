using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.Web.Filters;

/// <summary>Refuses with 404 a call on a conversation that does not carry its seal from the channel that opened it.</summary>
/// <remarks>
/// The refusal is the very 404 <see cref="KnownConversationFilter"/> answers, so a caller holding a stolen
/// id cannot even learn that the conversation exists. It runs right after that filter, before anything
/// that could spend or record on the conversation's behalf.
/// </remarks>
/// <param name="conversationSealService">Checks the presented seal against the one on record.</param>
/// <param name="logger">Records the refused call by conversation id alone.</param>
public sealed class ConversationSealFilter(
    IConversationSealService conversationSealService,
    ILogger logger) : IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // The conversation KnownConversationFilter already found on record
        string conversationId = context.RouteData.Values[KnownConversationFilter.ConversationIdRouteKey]?.ToString() ?? string.Empty;

        // Left by ChannelAuthenticationFilter, which proved it: the seal only opens to the channel it was issued to
        string issuer = context.HttpContext.Items[ChannelAuthenticationFilter.IssuerItemKey] as string ?? string.Empty;
        string? presentedSeal = context.HttpContext.Request.Headers[StartConversationResponse.SealHeader].FirstOrDefault();

        if (!await conversationSealService.VerifyAsync(conversationId, issuer, presentedSeal))
        {
            logger.LogWarning("{Action} for conversation {ConversationId} without its seal; returning 404",
                context.ActionDescriptor.DisplayName, conversationId);
            context.Result = new NotFoundObjectResult(new { error = "Conversation not found", conversationId });
            return;
        }

        await next();
    }
}