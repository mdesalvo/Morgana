using System.Text.RegularExpressions;
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
            await channelService.SendMessageAsync(new ChannelMessage
            {
                ConversationId = conversationId,
                Text = rateLimitViolation,
                MessageType = "system_warning",
                ErrorReason = "rate_limit_exceeded",
                AgentName = "Morgana",
                AgentCompleted = false
            });

            // A window that reports no wait still gets a minute, so a client never retries in a tight loop
            context.HttpContext.Response.Headers.Append("Retry-After", rateLimitResult.RetryAfterSeconds?.ToString() ?? "60");
            context.Result = new ObjectResult(new
            {
                error = "Rate limit exceeded",
                violatedLimit = rateLimitResult.ViolatedLimit,
                retryAfterSeconds = rateLimitResult.RetryAfterSeconds,
                message = rateLimitViolation
            }) { StatusCode = StatusCodes.Status429TooManyRequests };
            return;
        }

        // The rate limit caps call frequency, the dust budget caps tokens: once spent the conversation
        // is terminal and the user must start a new one
        if (await dustLimitService.IsOverBudgetAsync(conversationId))
        {
            logger.LogWarning("Dust budget exhausted for conversation {ConversationId}", conversationId);

            await channelService.SendMessageAsync(new ChannelMessage
            {
                ConversationId = conversationId,
                Text = dustLimitingOptions.Value.ErrorMessage,
                MessageType = "error",
                ErrorReason = "dust_budget_exhausted",
                AgentName = "Morgana",
                AgentCompleted = false
            });

            context.Result = new ObjectResult(new
            {
                error = "Dust budget exhausted",
                message = dustLimitingOptions.Value.ErrorMessage
            }) { StatusCode = StatusCodes.Status429TooManyRequests };
            return;
        }

        await next();
    }

    /// <summary>The text authored under Morgana:RateLimiting for the violated window, {limit} filled in.</summary>
    private string GetRateLimitErrorMessage(Records.RateLimitResult result)
    {
        string message = result.ViolatedLimit switch
        {
            { } s when s.Contains("PerMinute") => rateLimitOptions.Value.ErrorMessagePerMinute,
            { } s when s.Contains("PerHour")   => rateLimitOptions.Value.ErrorMessagePerHour,
            { } s when s.Contains("PerDay")    => rateLimitOptions.Value.ErrorMessagePerDay,
            _ => rateLimitOptions.Value.ErrorMessageDefault
        };

        if (message.Contains("{limit}") && result.ViolatedLimit != null)
        {
            Match match = Regex.Match(result.ViolatedLimit, @"\((\d+)\)");
            if (match.Success)
                message = message.Replace("{limit}", match.Groups[1].Value);
        }

        return message;
    }
}