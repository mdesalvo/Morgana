using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using Morgana.AI;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.Web.Filters;

/// <summary>Holds a call that starts work on a conversation to its rate limit, then to its dust budget.</summary>
public sealed class ConversationLimitsFilter(
    IRateLimitService rateLimitService,
    IOptions<Records.RateLimitOptions> rateLimitOptions,
    IDustLimitService dustLimitService,
    IOptions<Records.DustLimitingOptions> dustLimitingOptions,
    IChannelService channelService,
    ILogger logger) : IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // The conversation KnownConversationFilter already found on record
        string conversationId = context.RouteData.Values[KnownConversationFilter.ConversationIdRouteKey]?.ToString() ?? string.Empty;

        // Checking also records the call, creating the conversation's database: KnownConversationFilter must run first
        Records.RateLimitResult rateLimitResult = await rateLimitService.CheckAndRecordAsync(conversationId);
        if (!rateLimitResult.IsAllowed)
        {
            logger.LogWarning("Rate limit exceeded for conversation {ConversationId}: {ViolatedLimit}", conversationId, rateLimitResult.ViolatedLimit);

            // The user hears why over the channel; the 429 below is for the client, which does not show it
            string rateLimitViolation = GetRateLimitErrorMessage(rateLimitResult);
            await channelService.SendMessageAsync(RefusalOf(context, conversationId, rateLimitViolation, ChannelMessageTypes.SystemWarning, ChannelErrorReasons.RateLimitExceeded));

            // A window that reports no wait still gets a minute, so a client never retries in a tight loop
            context.HttpContext.Response.Headers.Append("Retry-After", rateLimitResult.RetryAfterSeconds?.ToString(CultureInfo.InvariantCulture) ?? "60");
            context.Result = new ObjectResult(new
            {
                error = "Rate limit exceeded",
                violatedLimit = rateLimitResult.ViolatedLimit,
                retryAfterSeconds = rateLimitResult.RetryAfterSeconds,
                message = rateLimitViolation
            }) { StatusCode = StatusCodes.Status429TooManyRequests };
            // The refused call stops here, so it never reaches its action.
            return;
        }

        // The rate limit caps call frequency, the dust budget caps tokens: once spent the conversation
        // is terminal and the user must start a new one
        if (await dustLimitService.IsOverBudgetAsync(conversationId))
        {
            logger.LogWarning("Dust budget exhausted for conversation {ConversationId}", conversationId);

            // The dust refusal is shown to the user over the channel, before the client receives its 429.
            await channelService.SendMessageAsync(RefusalOf(context, conversationId, dustLimitingOptions.Value.ErrorMessage, ChannelMessageTypes.Error, ChannelErrorReasons.DustBudgetExhausted));

            // The 429 tells the client that the dust budget is spent, with the same wording the user read.
            context.Result = new ObjectResult(new
            {
                error = "Dust budget exhausted",
                message = dustLimitingOptions.Value.ErrorMessage
            }) { StatusCode = StatusCodes.Status429TooManyRequests };
            // The refused call stops here, so it never reaches its action.
            return;
        }

        // Both limits hold: the call reaches its action.
        await next();
    }

    /// <summary>
    /// The message telling the user why the call was refused. A refused message is a notice in the conversation
    /// of <paramref name="messageType"/>; a refused command is that command's outcome, delivered as its finished
    /// frame, since a command leaves no line in the conversation.
    /// </summary>
    private ChannelMessage RefusalOf(ActionExecutingContext context, string conversationId, string text, string messageType, string errorReason)
    {
        // The frame carries the name and the run the channel asked for, which is the run it waits on an outcome for
        CommandProgress? outcomeFrame = context.ActionArguments.Values.OfType<ExecuteCommandRequest>().FirstOrDefault() is { } commandRequest
            ? new CommandProgress(commandRequest.Name, "refused", 0, 1, Finished: true, commandRequest.InvocationId)
            : null;

        // The notice the user reads: Morgana's own voice, never an agent's and never a completed turn.
        return new ChannelMessage
        {
            ConversationId = conversationId,
            Text = text,
            MessageType = outcomeFrame is null ? messageType : ChannelMessageTypes.System,

            // Kept on a command's outcome too: a spent budget ends the conversation whatever asked for more of it
            ErrorReason = errorReason,
            AgentName = Constants.Morgana,
            AgentCompleted = false,
            Progress = outcomeFrame
        };
    }

    /// <summary>The text authored under Morgana:RateLimiting for the violated window, {limit} filled in.</summary>
    private string GetRateLimitErrorMessage(Records.RateLimitResult result)
    {
        // The violated window picks the authored text; a denial that names none reads the default one.
        string message = result.ViolatedWindow switch
        {
            // A denial in the per-minute window reads the per-minute wording.
            Records.RateLimitWindow.PerMinute => rateLimitOptions.Value.ErrorMessagePerMinute,
            // A denial in the per-hour window reads the per-hour wording.
            Records.RateLimitWindow.PerHour   => rateLimitOptions.Value.ErrorMessagePerHour,
            // A denial in the per-day window reads the per-day wording.
            Records.RateLimitWindow.PerDay    => rateLimitOptions.Value.ErrorMessagePerDay,
            // A denial that names no window reads the default wording.
            _ => rateLimitOptions.Value.ErrorMessageDefault
        };

        // The authored text may promise the number: it is the cap of the violated window.
        if (result.ViolatedCap is { } violatedCap)
            message = message.Replace("{limit}", violatedCap.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

        // A text with no placeholder goes out as authored.
        return message;
    }
}