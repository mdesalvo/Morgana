using Akka.Actor;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Morgana.Actors;
using Morgana.AI.Extensions;
using Morgana.Hubs;
using static Morgana.Records;

namespace Morgana.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConversationController : ControllerBase
{
    private readonly ActorSystem actorSystem;
    private readonly ILogger logger;
    private readonly IHubContext<ConversationHub> signalrContext;

    public ConversationController(
        ActorSystem actorSystem,
        ILogger logger,
        IHubContext<ConversationHub> signalrContext)
    {
        this.actorSystem = actorSystem;
        this.logger = logger;
        this.signalrContext = signalrContext;
    }

    [HttpPost("start")]
    public async Task<IActionResult> StartConversation([FromBody] StartConversationRequest request)
    {
        try
        {
            logger.LogInformation($"Starting conversation {request.ConversationId}");

            IActorRef manager = await actorSystem.GetOrCreateActor<ConversationManagerActor>("manager", request.ConversationId);

            ConversationCreated? conversationCreated = await manager.Ask<ConversationCreated>(
                new CreateConversation(request.ConversationId));

            logger.LogInformation($"Started conversation {conversationCreated.ConversationId}");

            return Ok(new
            {
                conversationId = conversationCreated.ConversationId,
                message = "Conversation started successfully"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start conversation");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{conversationId}/end")]
    public async Task<IActionResult> EndConversation(string conversationId)
    {
        try
        {
            logger.LogInformation($"Ending conversation {conversationId}");

            IActorRef manager = await actorSystem.GetOrCreateActor<ConversationManagerActor>("manager", conversationId);

            manager.Tell(new TerminateConversation(conversationId));

            logger.LogInformation($"Ended conversation {conversationId}");

            return Ok(new { message = "Conversation ended" });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to end conversation {conversationId}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{conversationId}/message")]
    public async Task<IActionResult> SendMessage([FromBody] SendMessageRequest request)
    {
        try
        {
            logger.LogInformation($"Sending message conversation {request.ConversationId}");

            IActorRef manager = await actorSystem.GetOrCreateActor<ConversationManagerActor>("manager", request.ConversationId);

            manager.Tell(new UserMessage(
                request.ConversationId,
                request.Text,
                DateTime.UtcNow
            ));

            logger.LogInformation($"Message sent to conversation {request.ConversationId}");

            return Accepted(new
            {
                conversationId = request.ConversationId,
                message = "Message processing started",
                note = "Response will be sent via SignalR"
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, $"Failed to send message to conversation {request.ConversationId}");
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow,
            actorSystem = actorSystem.WhenTerminated.IsCompleted ? "terminated" : "running"
        });
    }
}