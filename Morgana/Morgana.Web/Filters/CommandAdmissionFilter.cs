using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Morgana.AI.Interfaces;
using Morgana.Contracts;

namespace Morgana.Web.Filters;

/// <summary>Refuses with 400 a command request the command itself could not run.</summary>
/// <param name="commandRegistryService">Resolves the name and holds each command's declared rules.</param>
/// <param name="conversationPersistenceService">Tells whether an agent is carrying the conversation.</param>
/// <param name="logger">Records the refused request.</param>
public sealed class CommandAdmissionFilter(
    ICommandRegistryService commandRegistryService,
    IConversationPersistenceService conversationPersistenceService,
    ILogger logger) : IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // The conversation KnownConversationFilter already found on record
        string conversationId = context.RouteData.Values[KnownConversationFilter.ConversationIdRouteKey]?.ToString() ?? string.Empty;
        // Null when the body could not be read as a command request, which then names no command
        ExecuteCommandRequest? request = context.ActionArguments.Values.OfType<ExecuteCommandRequest>().FirstOrDefault();

        // An unknown name is the channel's mistake, not the user's: nothing is pushed to the conversation
        if (request is null || commandRegistryService.ResolveCommand(request.Name) is not { } command)
        {
            logger.LogWarning("Unknown command '{CommandName}' for conversation {ConversationId}", request?.Name, conversationId);
            context.Result = new BadRequestObjectResult(new { error = "Unknown command", name = request?.Name });
            return;
        }

        // While Morgana holds the conversation herself there is no agent's side to act on: what she says
        // belongs to no agent
        if (command.Descriptor.RequiresActiveAgent
            && await conversationPersistenceService.GetMostRecentActiveAgentAsync(conversationId) is not { Length: > 0 })
        {
            logger.LogWarning("Command '{CommandName}' needs an agent carrying conversation {ConversationId}, which none is",
                command.Descriptor.Name, conversationId);
            context.Result = new BadRequestObjectResult(new { error = "Command requires an agent carrying the conversation", name = command.Descriptor.Name });
            return;
        }

        // Options are judged by the command's own rule, the same one the channel's form used
        if (command.Descriptor.DescribeOptionProblem(request.Options) is { } optionProblem)
        {
            logger.LogWarning("Command '{CommandName}' was asked for with options it cannot take on conversation {ConversationId}: {OptionProblem}",
                command.Descriptor.Name, conversationId, optionProblem);
            context.Result = new BadRequestObjectResult(new { error = optionProblem, name = command.Descriptor.Name });
            return;
        }

        // A command that cannot be taken back runs only on a Yes the channel says it obtained
        if (command.Descriptor.RequiresConfirmation && !request.Confirmed)
        {
            logger.LogWarning("Command '{CommandName}' needs confirmation and none was carried; refusing for conversation {ConversationId}",
                command.Descriptor.Name, conversationId);
            context.Result = new BadRequestObjectResult(new { error = "Command requires confirmation", name = command.Descriptor.Name });
            return;
        }

        // Every rule the command declares holds: only the user's limits stand between it and running
        await next();
    }
}