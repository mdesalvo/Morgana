using Microsoft.AspNetCore.Mvc;
using Morgana.AI.Interfaces;
using Morgana.Contracts;
using Morgana.Web.Filters;

namespace Morgana.Web.Controllers;

/// <summary>REST API for the commands this installation publishes: their catalogue and their execution.</summary>
// A command works on the conversation's record with its own services: nothing here reaches the actor system
[ApiController]
[Route("api/morgana")]
[TypeFilter<ChannelAuthenticationFilter>]
public class CommandController(
    ICommandRegistryService commandRegistryService,
    ILogger logger) : ControllerBase
{
    /// <summary>Lists the published commands, for a channel to offer in its palette next to its own.</summary>
    /// <returns>200 OK with a <see cref="CommandCatalogResponse"/>, empty when no command is installed.</returns>
    [HttpGet("commands")]
    public IActionResult GetCommandCatalog()
    {
        // The same catalogue for every channel: which commands a channel shows is the channel's choice
        return Ok(new CommandCatalogResponse(commandRegistryService.GetCatalog()));
    }

    /// <summary>Runs a published command on a conversation; its outcome reaches the user over the channel.</summary>
    /// <returns>
    /// 202 Accepted once the command has run.
    /// 400 Bad Request on an unknown name, options the command does not take, a missing confirmation or no agent to act on.
    /// 404 Not Found if the conversation was never started.
    /// 429 Too Many Requests on the same limits a message meets.
    /// 500 Internal Server Error on failure.
    /// </returns>
    [HttpPost("conversation/{conversationId}/command")]
    [TypeFilter<KnownConversationFilter>(Order = 1)]
    // Admitted before the limits, so a request the channel got wrong never costs the user
    [TypeFilter<CommandAdmissionFilter>(Order = 2)]
    // A command fired by the user may spend tokens: it meets the limits a message meets
    [TypeFilter<ConversationLimitsFilter>(Order = 3)]
    public async Task<IActionResult> ExecuteCommand(string conversationId, [FromBody] ExecuteCommandRequest request)
    {
        // Admitted by CommandAdmissionFilter, which resolved this same name
        ICommand command = commandRegistryService.ResolveCommand(request.Name)!;

        try
        {
            // The command answers the user itself over the channel, so the HTTP reply only acknowledges it ran
            logger.LogInformation("Running command '{CommandName}' on conversation {ConversationId}", command.Descriptor.Name, conversationId);
            // A channel that left an option out gets the command's own default, exactly as a channel drawing
            // the form would have sent it: the values a command reads never depend on who called it
            await command.ExecuteAsync(conversationId, command.Descriptor.ApplyDefaults(request.Options));

            return Accepted(new { conversationId, command = command.Descriptor.Name });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run command '{CommandName}' on conversation {ConversationId}", command.Descriptor.Name, conversationId);
            return StatusCode(500, new { error = ex.Message });
        }
    }
}